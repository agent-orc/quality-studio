import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  findingKey,
  diffAgainstBaseline,
} from '../scripts/check-dotnet-whitespace-baseline.mjs';

const baselinePath = fileURLToPath(
  new URL('../.quality/style/dotnet-whitespace.baseline.json', import.meta.url),
);

test('findingKey is stable per file/line/char/description', () => {
  const finding = { line: 5, char: 9, description: 'Fix whitespace formatting. Delete 4 characters.' };
  assert.equal(
    findingKey('src/Foo.cs', finding),
    'src/Foo.cs:5:9:Fix whitespace formatting. Delete 4 characters.',
  );
});

test('diffAgainstBaseline reports no drift and no resolutions when current matches baseline exactly', () => {
  const baseline = {
    entries: [{ file: 'src/Foo.cs', findings: [{ line: 1, char: 1, description: 'd' }] }],
  };
  const current = [{ file: 'src/Foo.cs', findings: [{ line: 1, char: 1, description: 'd' }] }];

  const { newDrift, resolved } = diffAgainstBaseline(current, baseline);
  assert.deepEqual(newDrift, []);
  assert.deepEqual(resolved, []);
});

test('diffAgainstBaseline flags a finding not present in the baseline', () => {
  const baseline = { entries: [] };
  const current = [{ file: 'src/New.cs', findings: [{ line: 3, char: 5, description: 'd' }] }];

  const { newDrift, resolved } = diffAgainstBaseline(current, baseline);
  assert.equal(newDrift.length, 1);
  assert.equal(newDrift[0].file, 'src/New.cs');
  assert.deepEqual(resolved, []);
});

test('diffAgainstBaseline reports a baseline finding as resolved once it disappears', () => {
  const baseline = {
    entries: [{ file: 'src/Foo.cs', findings: [{ line: 1, char: 1, description: 'd' }] }],
  };
  const current = [];

  const { newDrift, resolved } = diffAgainstBaseline(current, baseline);
  assert.deepEqual(newDrift, []);
  assert.equal(resolved.length, 1);
  assert.equal(resolved[0].file, 'src/Foo.cs');
});

test('checked-in baseline file has the expected shape', () => {
  const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
  assert.ok(Array.isArray(baseline.entries));
  assert.ok(baseline.entries.length > 0);
  for (const entry of baseline.entries) {
    assert.equal(typeof entry.file, 'string');
    assert.ok(Array.isArray(entry.findings));
    for (const finding of entry.findings) {
      assert.equal(typeof finding.line, 'number');
      assert.equal(typeof finding.char, 'number');
      assert.equal(typeof finding.description, 'string');
    }
  }
});
