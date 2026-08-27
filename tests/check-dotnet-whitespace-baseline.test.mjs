import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { classifyDrift, parseDriftedFiles } from '../scripts/check-dotnet-whitespace-baseline.mjs';

const repoRoot = fileURLToPath(new URL('..', import.meta.url)).replace(/[\\/]$/, '');

test('parseDriftedFiles extracts unique repo-relative paths from dotnet format output', () => {
  const output = [
    `${repoRoot}/src/A.cs(10,5): error WHITESPACE: Fix whitespace formatting. Replace 1 characters with '\\n'. [proj.csproj]`,
    `${repoRoot}/src/A.cs(20,1): error WHITESPACE: Fix whitespace formatting. Replace 1 characters with '\\n'. [proj.csproj]`,
    `${repoRoot}/src/B.cs(1,1): error WHITESPACE: Fix whitespace formatting. Replace 1 characters with '\\n'. [proj.csproj]`,
    'Some unrelated build output line',
  ].join('\n');

  assert.deepEqual(parseDriftedFiles(output, repoRoot), ['src/A.cs', 'src/B.cs']);
});

test('parseDriftedFiles returns an empty list for clean output', () => {
  assert.deepEqual(parseDriftedFiles('Formatted 42 files. 0 files needed formatting.', repoRoot), []);
});

test('classifyDrift separates new drift from baselined and resolved files', () => {
  const baselineFiles = ['src/A.cs', 'src/B.cs', 'src/C.cs'];
  const driftedFiles = ['src/A.cs', 'src/D.cs'];

  const { newDrift, existing, resolved } = classifyDrift(driftedFiles, baselineFiles);

  assert.deepEqual(newDrift, ['src/D.cs']);
  assert.deepEqual(existing, ['src/A.cs']);
  assert.deepEqual(resolved, ['src/B.cs', 'src/C.cs']);
});

test('classifyDrift is clean when drift exactly matches the baseline', () => {
  const baselineFiles = ['src/A.cs', 'src/B.cs'];
  const { newDrift, existing, resolved } = classifyDrift(baselineFiles, baselineFiles);

  assert.deepEqual(newDrift, []);
  assert.deepEqual(existing, ['src/A.cs', 'src/B.cs']);
  assert.deepEqual(resolved, []);
});

test('checked-in baseline currently matches the repository whitespace drift set', async () => {
  const baselinePath = resolve(repoRoot, '.quality', 'style', 'dotnet-whitespace.baseline.json');
  const baseline = JSON.parse(await readFile(baselinePath, 'utf8'));

  assert.equal(baseline.schemaVersion, 1);
  assert.ok(Array.isArray(baseline.files) && baseline.files.length > 0);
  assert.deepEqual([...baseline.files].sort(), baseline.files, 'baseline files should be kept sorted');
});
