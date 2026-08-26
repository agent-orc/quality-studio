import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('..', import.meta.url));
const checker = resolve(repositoryRoot, 'scripts', 'rule-checks', 'design-tokens-check.mjs');
const fixture = resolve(repositoryRoot, 'tests', 'Fixtures', 'rule-checks', 'design-tokens');

test('design token check emits named findings and ignores declarations, comments, hairlines, and non-token literals', async () => {
  const outputDirectory = await mkdtemp(join(tmpdir(), 'qs-rule-check-'));
  const reportPath = join(outputDirectory, 'design-tokens.sarif.json');
  try {
    await run(checker, fixture, reportPath);
    const report = JSON.parse(await readFile(reportPath, 'utf8'));
    const results = report.runs[0].results;

    assert.equal(results.filter((result) => result.ruleId === 'QS-NG-003').length, 3);
    assert.equal(results.filter((result) => result.ruleId === 'QS-NG-004').length, 1);
    assert.deepEqual(
      [...new Set(results.map((result) => result.ruleId))].sort(),
      ['QS-NG-003', 'QS-NG-004'],
    );
    assert.ok(results.every((result) =>
      result.locations.every((location) =>
        location.physicalLocation.artifactLocation.uri.startsWith('tests/Fixtures/rule-checks/'))));
  } finally {
    await rm(outputDirectory, { recursive: true, force: true });
  }
});

test('design token check accepts a single CSS file target', async () => {
  const outputDirectory = await mkdtemp(join(tmpdir(), 'qs-rule-check-file-'));
  const reportPath = join(outputDirectory, 'design-tokens.sarif.json');
  try {
    await run(checker, join(fixture, 'first.css'), reportPath);
    const report = JSON.parse(await readFile(reportPath, 'utf8'));
    assert.equal(report.runs[0].results.filter((result) => result.ruleId === 'QS-NG-003').length, 2);
    assert.equal(report.runs[0].results.filter((result) => result.ruleId === 'QS-NG-004').length, 0);
  } finally {
    await rm(outputDirectory, { recursive: true, force: true });
  }
});

function run(script, target, reportPath) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(process.execPath, [script, target, reportPath], {
      cwd: repositoryRoot,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stderr = '';
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('exit', (code) => {
      if (code === 0) resolvePromise();
      else reject(new Error(`design token check exited ${code}: ${stderr}`));
    });
  });
}
