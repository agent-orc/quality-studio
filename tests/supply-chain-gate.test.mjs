import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const gate = resolve('scripts/assert-no-vulnerable-packages.mjs');

test('NuGet advisory gate accepts a clean report', async () => {
  await withReport({ projects: [{ frameworks: [{ topLevelPackages: [] }] }] }, (report) => {
    const result = spawnSync(process.execPath, [gate, report], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
  });
});

test('NuGet advisory gate rejects a vulnerable fixture', async () => {
  await withReport({
    projects: [{
      frameworks: [{
        topLevelPackages: [{ id: 'Vulnerable.Package', vulnerabilities: [{ severity: 'high' }] }],
      }],
    }],
  }, (report) => {
    const result = spawnSync(process.execPath, [gate, report], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /found 1 vulnerable package entry/i);
  });
});

async function withReport(document, assertion) {
  const directory = await mkdtemp(join(tmpdir(), 'quality-studio-advisory-gate-'));
  try {
    const report = join(directory, 'report.json');
    await writeFile(report, JSON.stringify(document));
    assertion(report);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}
