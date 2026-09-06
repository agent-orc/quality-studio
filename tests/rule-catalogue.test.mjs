import test from 'node:test';
import assert from 'node:assert/strict';
import { cp, mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const catalogueRelativePath = join('src', 'AgentOrchestrator.CodeQuality', 'catalogues', 'rule-catalogue.v1.json');

/** A sandbox holding only what the generator reads and writes, so tests never touch the checkout. */
async function sandbox() {
  const root = await mkdtemp(join(tmpdir(), 'qs-rule-catalogue-'));
  await mkdir(join(root, 'scripts'), { recursive: true });
  await mkdir(join(root, 'schemas'), { recursive: true });
  await mkdir(join(root, 'src', 'AgentOrchestrator.CodeQuality', 'catalogues'), { recursive: true });
  await cp(join(repoRoot, 'scripts', 'sync-rule-catalogue.mjs'), join(root, 'scripts', 'sync-rule-catalogue.mjs'));
  await cp(join(repoRoot, 'schemas', 'rule-catalogue.v1.schema.json'),
    join(root, 'schemas', 'rule-catalogue.v1.schema.json'));
  await cp(join(repoRoot, 'rules'), join(root, 'rules'), { recursive: true });
  return root;
}

function run(root, ...args) {
  return new Promise(resolvePromise => {
    const child = spawn(process.execPath, [join(root, 'scripts', 'sync-rule-catalogue.mjs'), ...args],
      { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('close', code => resolvePromise({ code, stdout, stderr }));
  });
}

test('generation is deterministic and matches the committed catalogue', async () => {
  const root = await sandbox();

  const first = await run(root);
  assert.equal(first.code, 0, first.stderr);
  const firstBytes = await readFile(join(root, catalogueRelativePath));

  const second = await run(root);
  assert.equal(second.code, 0, second.stderr);
  const secondBytes = await readFile(join(root, catalogueRelativePath));

  assert.deepEqual(firstBytes, secondBytes, 'two runs over the same rule tree must be byte-identical');
  const committed = await readFile(join(repoRoot, catalogueRelativePath));
  assert.deepEqual(firstBytes, committed, 'the committed catalogue is stale; run npm run rules:sync');
});

test('every authored rule reaches the catalogue with its routing fields', async () => {
  const catalogue = JSON.parse(await readFile(join(repoRoot, catalogueRelativePath), 'utf8'));
  const authored = [];
  for (const technology of ['angular', 'dotnet', 'generic']) {
    let files = [];
    try {
      files = await readdir(join(repoRoot, 'rules', technology));
    } catch {
      continue;
    }
    authored.push(...files.filter(name => name.endsWith('.md')));
  }

  assert.equal(catalogue.entries.length, authored.length);
  assert.deepEqual(catalogue.entries.map(entry => entry.id), [...catalogue.entries.map(entry => entry.id)].sort());
  for (const entry of catalogue.entries) {
    assert.ok(entry.kinds.length > 0, `${entry.id} declares no review kind`);
    assert.ok(['angular', 'dotnet', 'generic'].includes(entry.technology), `${entry.id} has no technology`);
    assert.ok(entry.detection.length > 0, `${entry.id} has no detection guidance`);
    assert.equal(entry.changeHistory[0].version, entry.version, `${entry.id} change history is not newest-first`);
  }
  const changelog = await readFile(join(repoRoot, 'rules', 'CHANGELOG.md'), 'utf8');
  assert.match(changelog, new RegExp(`^## ${catalogue.catalogueVersion.replace(/\./g, '\\.')} `, 'm'));
});

test('--check reports drift after a rule changes', async () => {
  const root = await sandbox();
  assert.equal((await run(root)).code, 0);
  assert.equal((await run(root, '--check')).code, 0);

  const rulePath = join(root, 'rules', 'dotnet', 'QS-CS-003-async-hygiene.md');
  const rule = await readFile(rulePath, 'utf8');
  await writeFile(rulePath, rule.replace('severity: high', 'severity: low'));

  const drifted = await run(root, '--check');
  assert.equal(drifted.code, 1);
  assert.match(drifted.stderr, /drifted/);
});

test('a rule missing a required section fails before anything is written', async () => {
  const root = await sandbox();
  const rulePath = join(root, 'rules', 'dotnet', 'QS-CS-003-async-hygiene.md');
  const rule = await readFile(rulePath, 'utf8');
  await writeFile(rulePath, rule.replace(/## Detection[\s\S]*?(?=## Good example)/, ''));

  const result = await run(root);

  assert.equal(result.code, 1);
  assert.match(result.stderr, /## Detection/);
  await assert.rejects(readFile(join(root, catalogueRelativePath)));
});

test('a rule whose id contradicts its directory fails validation', async () => {
  const root = await sandbox();
  const rulePath = join(root, 'rules', 'dotnet', 'QS-CS-003-async-hygiene.md');
  const rule = await readFile(rulePath, 'utf8');
  await writeFile(rulePath, rule.replace('technology: dotnet', 'technology: angular'));

  const result = await run(root);

  assert.equal(result.code, 1);
  assert.match(result.stderr, /does not match its 'dotnet' directory/);
});

test('the generator writes nothing time- or checkout-dependent', async () => {
  const catalogue = await readFile(join(repoRoot, catalogueRelativePath), 'utf8');
  const document = JSON.parse(catalogue);

  assert.deepEqual(Object.keys(document), ['$schema', 'schemaVersion', 'catalogueVersion', 'entries']);
  assert.doesNotMatch(catalogue, /generatedAt|generatedFrom|"commit"/);
});
