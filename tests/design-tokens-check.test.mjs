import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

test('design-token pre-check distinguishes covered literals from scale extensions', async () => {
  const resultsRoot = process.env.JOB_RESULTS_DIR ?? resolve('results');
  await mkdir(resultsRoot, { recursive: true });
  const fixture = await mkdtemp(join(resultsRoot, 'design-token-check-'));
  try {
    await writeFile(join(fixture, 'one.css'), '.one { margin: 8px; color: #fff; width: 17px; }\n');
    await writeFile(join(fixture, 'two.css'), '.two { gap: 17px; }\n');
    const report = join(fixture, 'report.sarif.json');

    const run = spawnSync(process.execPath,
      ['scripts/rule-checks/design-tokens-check.mjs', fixture, report],
      { cwd: resolve('.'), encoding: 'utf8' });

    assert.equal(run.status, 0, run.stderr);
    const sarif = JSON.parse(await readFile(report, 'utf8'));
    const results = sarif.runs[0].results;
    const covered = results.filter(result => result.ruleId === 'QS-NG-003');
    assert.equal(covered.length, 2);
    assert.ok(covered.some(result => result.message.text.includes("'8px' duplicates") &&
      result.message.text.includes('--studio-space-2')));
    assert.ok(covered.some(result => result.message.text.includes("'#fff' duplicates --studio-bg-surface")));
    const extension = results.filter(result => result.ruleId === 'QS-NG-004');
    assert.equal(extension.length, 1);
    assert.match(extension[0].message.text, /'17px' repeated across 2 stylesheets/);
  } finally {
    await rm(fixture, { recursive: true, force: true });
  }
});
