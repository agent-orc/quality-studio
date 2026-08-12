import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { enforceBaseline, findReport, parseCobertura, parseLcov } from '../scripts/check-coverage.mjs';

test('reads the aggregate line rate from Cobertura', () => {
  assert.equal(parseCobertura('<?xml version="1.0"?><coverage line-rate="0.625"><packages /></coverage>'), 0.625);
  assert.throws(() => parseCobertura('<coverage />'), /no coverage line-rate/);
});

test('aggregates all lcov source records', () => {
  assert.equal(parseLcov('SF:a.ts\nLF:4\nLH:3\nend_of_record\nSF:b.ts\nLF:6\nLH:2\nend_of_record\n'), 0.5);
  assert.throws(() => parseLcov('TN:\n'), /invalid LF\/LH totals/);
});

test('coverage can stay level or rise but cannot drop below the committed baseline', () => {
  assert.doesNotThrow(() => enforceBaseline('api', 0.8, 0.8));
  assert.doesNotThrow(() => enforceBaseline('api', 0.81, 0.8));
  assert.throws(() => enforceBaseline('api', 0.79, 0.8), /below the committed/);
});

test('requires exactly one generated report', async context => {
  const root = await mkdtemp(join(tmpdir(), 'quality-studio-coverage-contract-'));
  context.after(() => rm(root, { recursive: true, force: true }));

  await assert.rejects(() => findReport(root, 'lcov.info'), /found 0/);
  await mkdir(join(root, 'run-1'));
  await writeFile(join(root, 'run-1', 'lcov.info'), 'LF:1\nLH:1\n');
  assert.equal(await findReport(root, 'lcov.info'), join(root, 'run-1', 'lcov.info'));
  await mkdir(join(root, 'run-2'));
  await writeFile(join(root, 'run-2', 'lcov.info'), 'LF:1\nLH:1\n');
  await assert.rejects(() => findReport(root, 'lcov.info'), /found 2/);
});
