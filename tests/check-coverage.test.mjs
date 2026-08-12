import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluateCoverage, parseCobertura, parseCoberturaPackages, parseLcov, parseLcovSources } from '../scripts/check-coverage.mjs';

test('Cobertura parser reads and validates the document line rate', () => {
  assert.equal(parseCobertura('<?xml version="1.0"?><coverage line-rate="0.625" branch-rate="0.5"/>'), 0.625);
  assert.throws(() => parseCobertura('<coverage branch-rate="0.5"/>'), /no numeric coverage line-rate/);
  assert.throws(() => parseCobertura('<coverage line-rate="1.5"/>'), /outside 0\.\.1/);
});

test('Cobertura parser exposes package-level feature measurements', () => {
  const packages = parseCoberturaPackages('<packages><package name="Core" line-rate="0.8"/><package name="CLI" line-rate="0.5"/></packages>');
  assert.deepEqual(packages, { Core: 0.8, CLI: 0.5 });
});

test('lcov parser combines line totals across source files', () => {
  const document = ['TN:', 'SF:a.ts', 'LF:10', 'LH:8', 'end_of_record', 'SF:b.ts', 'LF:5', 'LH:2', 'end_of_record'].join('\n');
  assert.equal(parseLcov(document), 2 / 3);
  assert.deepEqual(parseLcovSources(document).sources, {
    'a.ts': { found: 10, hit: 8 },
    'b.ts': { found: 5, hit: 2 },
  });
  assert.throws(() => parseLcov('TN:\nend_of_record\n'), /no instrumented lines/);
});

test('coverage evaluator reports missing and regressed projects', () => {
  const baseline = {
    schemaVersion: 1,
    metric: 'line-rate',
    projects: { core: { minimum: 0.8, features: { library: { minimum: 0.82 } } }, api: { minimum: 0.7 }, frontend: { minimum: 0.6 } },
  };
  const result = evaluateCoverage(baseline, { core: 0.81, 'core/library': 0.8, api: 0.69 });
  assert.deepEqual(result.failures, [
    'core/library: 80.00% is below 82.00%',
    'api: 69.00% is below 70.00%',
    'frontend: report is missing',
  ]);
});
