import assert from 'node:assert/strict';
import test from 'node:test';
import { buildEconomyReport } from '../scripts/preflight-economy-core.mjs';

const match = (operations) => ({
  operations,
  snapshotMatched: true,
  routeMatched: true,
  falseClean: false,
  staleReuse: false,
  before: { inputTokens: operations * 100, outputTokens: operations * 20 },
  after: { inputTokens: operations * 75, outputTokens: operations * 15 },
  avoidedModelCalls: operations / 10,
  staticDurationMs: operations * 5,
});

test('economy report refuses a percentage below 30 matched operations', () => {
  const report = buildEconomyReport([match(29)], '2026-08-11T00:00:00Z');

  assert.equal(report.status, 'insufficient-evidence');
  assert.equal(report.matchedOperations, 29);
  assert.equal(report.savingsPercent, null);
});

test('economy report derives a percentage from 30 matched actual-usage operations', () => {
  const report = buildEconomyReport([match(15), match(15)], '2026-08-11T00:00:00Z');

  assert.equal(report.status, 'measured');
  assert.equal(report.matchedOperations, 30);
  assert.equal(report.savingsPercent, 25);
  assert.deepEqual(report.guardrails, {
    zeroFalseCleanResults: true,
    zeroStaleResultReuse: true,
    routesUnchanged: true,
  });
});

test('economy report rejects observations without explicit safety evidence', () => {
  const incomplete = { ...match(30) };
  delete incomplete.falseClean;

  const report = buildEconomyReport([incomplete], '2026-08-11T00:00:00Z');

  assert.equal(report.status, 'insufficient-evidence');
  assert.equal(report.matchedOperations, 0);
  assert.equal(report.rejectedMatches, 1);
  assert.deepEqual(report.guardrails, {
    zeroFalseCleanResults: null,
    zeroStaleResultReuse: null,
    routesUnchanged: null,
  });
});
