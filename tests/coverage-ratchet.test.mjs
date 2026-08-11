import test from 'node:test';
import assert from 'node:assert/strict';
import { compareCoverage, parseCobertura, parseLcov } from '../scripts/coverage-ratchet.mjs';

test('coverage parsers reject missing data and return schema-readable line rates', () => {
  assert.deepEqual(
    parseCobertura('<coverage lines-covered="75" lines-valid="100" line-rate="0.75"></coverage>'),
    { format: 'cobertura', covered: 75, valid: 100, rate: 0.75 },
  );
  assert.deepEqual(
    parseLcov('SF:file.ts\nLF:20\nLH:15\nend_of_record\n'),
    { format: 'lcov', covered: 15, valid: 20, rate: 0.75 },
  );
  assert.throws(() => parseLcov('TN:\n'), /invalid line totals/);
});

test('coverage ratchet reports decreases, missing reports, and unbaselined projects', () => {
  const baseline = { projects: { stable: { rate: 0.8 }, missing: { rate: 0.5 } } };
  const current = { projects: { stable: { rate: 0.79 }, added: { rate: 1 } } };

  assert.deepEqual(compareCoverage(baseline, current), [
    'stable: line coverage decreased from 80.00% to 79.00%',
    'missing: coverage report is missing',
    'added: no committed baseline exists',
  ]);
});
