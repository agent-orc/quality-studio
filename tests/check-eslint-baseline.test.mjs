import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { classifyFindings, toBaselineFindings } from '../scripts/check-eslint-baseline.mjs';

const repoRoot = fileURLToPath(new URL('..', import.meta.url)).replace(/[\\/]$/, '');

test('toBaselineFindings flattens ESLint JSON results into repo-relative findings', () => {
  const eslintJsonResults = [
    {
      filePath: `${repoRoot}/scripts/dev-stack.mjs`,
      messages: [
        { ruleId: 'no-unused-vars', line: 1 },
        { ruleId: 'no-unused-vars', line: 4 },
      ],
    },
    { filePath: `${repoRoot}/scripts/clean.mjs`, messages: [] },
  ];

  assert.deepEqual(toBaselineFindings(eslintJsonResults, repoRoot), [
    { file: 'scripts/dev-stack.mjs', ruleId: 'no-unused-vars', line: 1 },
    { file: 'scripts/dev-stack.mjs', ruleId: 'no-unused-vars', line: 4 },
  ]);
});

test('classifyFindings separates new findings from baselined and resolved ones', () => {
  const baselineFindings = [
    { file: 'a.mjs', ruleId: 'no-unused-vars', line: 1 },
    { file: 'b.mjs', ruleId: 'no-undef', line: 2 },
  ];
  const currentFindings = [
    { file: 'a.mjs', ruleId: 'no-unused-vars', line: 1 },
    { file: 'c.mjs', ruleId: 'no-console', line: 3 },
  ];

  const { newFindings, existing, resolved } = classifyFindings(currentFindings, baselineFindings);

  assert.deepEqual(newFindings, [{ file: 'c.mjs', ruleId: 'no-console', line: 3 }]);
  assert.deepEqual(existing, [{ file: 'a.mjs', ruleId: 'no-unused-vars', line: 1 }]);
  assert.deepEqual(resolved, [{ file: 'b.mjs', ruleId: 'no-undef', line: 2 }]);
});

test('classifyFindings is clean when current findings exactly match the baseline', () => {
  const findings = [{ file: 'a.mjs', ruleId: 'no-unused-vars', line: 1 }];
  const { newFindings, existing, resolved } = classifyFindings(findings, findings);

  assert.deepEqual(newFindings, []);
  assert.deepEqual(existing, findings);
  assert.deepEqual(resolved, []);
});

test('checked-in ESLint baseline is well-formed', async () => {
  const baselinePath = resolve(repoRoot, '.quality', 'style', 'eslint.baseline.json');
  const baseline = JSON.parse(await readFile(baselinePath, 'utf8'));

  assert.equal(baseline.schemaVersion, 1);
  assert.ok(Array.isArray(baseline.findings) && baseline.findings.length > 0);
  for (const finding of baseline.findings) {
    assert.equal(typeof finding.file, 'string');
    assert.equal(typeof finding.ruleId, 'string');
    assert.equal(typeof finding.line, 'number');
  }
});
