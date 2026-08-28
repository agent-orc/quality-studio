import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  findingKey,
  diffAgainstBaseline,
} from '../scripts/check-eslint-baseline.mjs';

const baselinePath = fileURLToPath(new URL('../.quality/style/eslint.baseline.json', import.meta.url));

test('findingKey ignores column and severity so unrelated churn does not break the key', () => {
  const a = findingKey('src/a.ts', { ruleId: 'no-unused-vars', line: 4, column: 1, severity: 1 });
  const b = findingKey('src/a.ts', { ruleId: 'no-unused-vars', line: 4, column: 99, severity: 2 });
  assert.equal(a, b);
});

test('diffAgainstBaseline is clean when current findings match the baseline', () => {
  const baseline = {
    entries: [{ file: 'src/a.ts', findings: [{ ruleId: 'no-unused-vars', line: 4, column: 1, severity: 1 }] }],
  };
  const current = [{ file: 'src/a.ts', findings: [{ ruleId: 'no-unused-vars', line: 4, column: 1, severity: 1 }] }];

  const { newDrift, resolved } = diffAgainstBaseline(current, baseline);
  assert.deepEqual(newDrift, []);
  assert.deepEqual(resolved, []);
});

test('diffAgainstBaseline flags a new finding not in the baseline', () => {
  const baseline = { entries: [] };
  const current = [{ file: 'src/b.ts', findings: [{ ruleId: 'no-undef', line: 1, column: 1, severity: 2 }] }];

  const { newDrift } = diffAgainstBaseline(current, baseline);
  assert.equal(newDrift.length, 1);
  assert.equal(newDrift[0].ruleId, 'no-undef');
});

test('diffAgainstBaseline reports a fixed baseline finding as resolved', () => {
  const baseline = {
    entries: [{ file: 'src/a.ts', findings: [{ ruleId: 'no-unused-vars', line: 4, column: 1, severity: 1 }] }],
  };
  const current = [];

  const { newDrift, resolved } = diffAgainstBaseline(current, baseline);
  assert.deepEqual(newDrift, []);
  assert.equal(resolved.length, 1);
});

test('checked-in baseline file has the expected shape', () => {
  const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
  assert.ok(Array.isArray(baseline.entries));
  assert.ok(baseline.entries.length > 0);
  for (const entry of baseline.entries) {
    assert.equal(typeof entry.file, 'string');
    for (const finding of entry.findings) {
      assert.equal(typeof finding.line, 'number');
    }
  }
});
