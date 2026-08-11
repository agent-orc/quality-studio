import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildEconomyReport } from './preflight-economy-core.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ledgerPath = resolve(repositoryRoot, process.argv[2] ?? '.quality/economy/matches.json');
const resultsDirectory = process.env.JOB_RESULTS_DIR
  ? resolve(process.env.JOB_RESULTS_DIR)
  : resolve(repositoryRoot, '.quality', 'economy');
const outputPath = resolve(resultsDirectory, 'preflight-economy-report.json');
let matches = [];
if (existsSync(ledgerPath)) {
  const ledger = JSON.parse(readFileSync(ledgerPath, 'utf8'));
  if (!Array.isArray(ledger.matches)) throw new Error('Economy ledger must contain a matches array.');
  matches = ledger.matches.map(resolveMatch);
}
const report = buildEconomyReport(matches);
mkdirSync(dirname(outputPath), { recursive: true });
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
console.log(`Economy report: ${report.status}; ${report.matchedOperations}/30 matched operations; ${outputPath}`);

function resolveMatch(match) {
  if (match.before && match.after) return match;
  if (!match.beforeRunId || !match.afterRunId) {
    throw new Error('Each economy match needs inline before/after usage or beforeRunId/afterRunId.');
  }
  const before = readRun(match.beforeRunId);
  const after = readRun(match.afterRunId);
  return {
    ...match,
    snapshotMatched: snapshotKey(before.manifest) === snapshotKey(after.manifest),
    routeMatched: routeKey(before.result) === routeKey(after.result),
    operations: before.result.economy?.modelCallsExecuted ?? before.result.usageOperations,
    before: before.result.usage,
    after: after.result.usage,
    avoidedModelCalls: after.result.economy?.modelCallsBlocked ?? 0,
    staticDurationMs: after.result.economy?.staticDurationMs ?? 0,
  };
}

function readRun(runId) {
  const directory = resolve(repositoryRoot, '.quality', 'runs', runId);
  return {
    manifest: JSON.parse(readFileSync(resolve(directory, 'manifest.json'), 'utf8')),
    result: JSON.parse(readFileSync(resolve(directory, 'result.json'), 'utf8')),
  };
}

function snapshotKey(manifest) {
  return JSON.stringify(manifest.targets.map(({ path, subjectHash }) => ({ path, subjectHash }))
    .sort((left, right) => left.path.localeCompare(right.path)));
}

function routeKey(result) {
  return JSON.stringify([result.kind, result.model, result.thinkingLevel, result.cli]);
}
