import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const verifier = resolve(repoRoot, 'scripts', 'verify-coverage-baseline.mjs');

test('coverage ratchet accepts schema-readable reports at or above the baseline', async () => {
  const fixture = await createFixture(0.5);
  const result = runVerifier(fixture);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /coverage ratchet passed/);
});

test('coverage ratchet rejects a coverage regression', async () => {
  const fixture = await createFixture(0.76);
  const result = runVerifier(fixture);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /dotnet-core line coverage regressed/);
});

async function createFixture(minimumLineRate) {
  const root = await mkdtemp(join(tmpdir(), 'quality-coverage-ratchet-'));
  const core = join(root, 'dotnet', 'core', 'run');
  const api = join(root, 'dotnet', 'api', 'run');
  const angular = join(root, 'lcov.info');
  const baseline = join(root, 'baseline.json');
  await mkdir(core, { recursive: true });
  await mkdir(api, { recursive: true });
  await writeFile(join(core, 'coverage.cobertura.xml'), '<coverage line-rate="0.75" lines-covered="3" lines-valid="4" />');
  await writeFile(join(api, 'coverage.cobertura.xml'), '<coverage line-rate="0.5" lines-covered="1" lines-valid="2" />');
  await writeFile(angular, 'TN:\nSF:src/app.ts\nLF:4\nLH:3\nend_of_record\n');
  await writeFile(baseline, JSON.stringify({
    schemaVersion: 1,
    projects: {
      'dotnet-core': { format: 'cobertura', report: 'core', minimumLineRate },
      'dotnet-api': { format: 'cobertura', report: 'api', minimumLineRate: 0.5 },
      angular: { format: 'lcov', minimumLineRate: 0.75 },
    },
  }));
  return { baseline, dotnetRoot: join(root, 'dotnet'), angular };
}

function runVerifier(fixture) {
  return spawnSync(process.execPath, [
    verifier,
    '--baseline', fixture.baseline,
    '--dotnet-root', fixture.dotnetRoot,
    '--angular', fixture.angular,
  ], { encoding: 'utf8' });
}
