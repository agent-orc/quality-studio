import assert from 'node:assert/strict';
import test from 'node:test';
import { compareObservations } from '../scripts/static-analysis-baseline-core.mjs';

const finding = (fingerprint, tool = 'eslint') => ({
  tool,
  path: 'frontend/src/app.ts',
  line: 1,
  column: 1,
  rule: 'fixture',
  fingerprint,
});

test('baseline comparison labels existing, new, and resolved observations', () => {
  const comparison = compareObservations(
    [finding('existing'), finding('resolved'), finding('dotnet-only', 'dotnet')],
    [finding('existing'), finding('new'), finding('dotnet-only', 'dotnet')],
  );

  assert.deepEqual(comparison.existing.map((item) => item.fingerprint), ['existing', 'dotnet-only']);
  assert.deepEqual(comparison.added.map((item) => item.fingerprint), ['new']);
  assert.deepEqual(comparison.resolved.map((item) => item.fingerprint), ['resolved']);
});

test('tool-specific checks do not reinterpret another tool baseline', () => {
  const comparison = compareObservations(
    [finding('eslint'), finding('dotnet', 'dotnet')],
    [finding('eslint')],
    new Set(['eslint']),
  );

  assert.equal(comparison.existing.length, 1);
  assert.equal(comparison.added.length, 0);
  assert.equal(comparison.resolved.length, 0);
});
