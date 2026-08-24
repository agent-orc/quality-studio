import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const hash = value => `sha256:${value.repeat(64).slice(0, 64)}`;
const subjectPath = 'src/QualityStudio.Api/Program.cs';
const tokens = (input, out) => ({ inputTokens: input, outputTokens: out, cachedInputTokens: 40, reasoningOutputTokens: 20, durationMs: 4200 });
const spend = (input, out) => ({ tokens: tokens(input, out), cost: null, currency: null, priceStatus: 'unavailable' });
const counters = { totalFiles: 1, completedFiles: 1, failedFiles: 0, skippedFiles: 0, usageOperations: 1 };

// Archived history rows: a healthy pair for comparison, plus the two degraded
// provenances the slice is meant to keep visible instead of hiding.
const historyRuns = [
  { runId: 'run-2026-08-24-b', repositoryId: 'default', createdAt: '2026-08-24T09:10:00Z', path: subjectPath, level: 'file', kind: 'code', outcome: 'done', complete: true, attempt: 1, startedAt: '2026-08-24T09:10:01Z', finishedAt: '2026-08-24T09:10:09Z', operations: 3, findings: 2, quality: { lowestGrade: 84, lowestBand: 'B', worstSecurityVerdict: null, activeFindings: 2, highestActiveSeverity: 'high' } },
  { runId: 'run-2026-08-23-a', repositoryId: 'default', createdAt: '2026-08-23T16:02:00Z', path: subjectPath, level: 'file', kind: 'code', outcome: 'done', complete: true, attempt: 2, startedAt: '2026-08-23T16:02:01Z', finishedAt: '2026-08-23T16:02:12Z', operations: 3, findings: 3, quality: { lowestGrade: 76, lowestBand: 'C', worstSecurityVerdict: null, activeFindings: 3, highestActiveSeverity: 'high' } },
  { runId: 'run-2026-08-22-sec', repositoryId: 'default', createdAt: '2026-08-22T11:30:00Z', path: 'src/QualityStudio.Api', level: 'folder', kind: 'security', outcome: 'capped', complete: false, attempt: 1, startedAt: '2026-08-22T11:30:01Z', finishedAt: '2026-08-22T11:33:40Z', operations: 2, findings: 1, quality: { lowestGrade: null, lowestBand: null, worstSecurityVerdict: 'needs-review', activeFindings: 1, highestActiveSeverity: 'medium' } },
  { runId: 'run-2026-08-21-corrupt', repositoryId: 'default', createdAt: null, path: null, level: null, kind: null, outcome: null, complete: null, attempt: null, startedAt: null, finishedAt: null, operations: 0, findings: 0, quality: null, errorCode: 'history-corrupt', error: 'attempt record is not valid JSON' },
  { runId: 'run-2026-08-20-legacy', repositoryId: 'default', createdAt: '2026-08-20T08:00:00Z', path: subjectPath, level: 'file', kind: 'code', outcome: 'legacy-usage-only', complete: null, attempt: null, startedAt: null, finishedAt: '2026-08-20T08:00:30Z', operations: 1, findings: 0, quality: null, provenance: 'legacy-usage-only', model: 'gpt-evidence', spend: spend(510, 120) },
];

const detailFor = runId => {
  const row = historyRuns.find(candidate => candidate.runId === runId) ?? historyRuns[0];
  return {
    run: {
      runId: row.runId, repositoryId: 'default', createdAt: row.createdAt, subject: { id: 'program', name: 'Program.cs', path: row.path },
      level: row.level, kind: row.kind, targets: [{ id: 'program', name: 'Program.cs', path: row.path, subjectHash: hash('c') }],
      configuration: { model: 'gpt-evidence', thinkingLevel: 'high', cliType: 'codex', force: false, tokenCap: null, costCap: null, estimate: null, recommendation: null, routeOverride: false },
      sourceRevision: { commit: '0f15a39', dirty: false },
    },
    attempt: {
      runId: row.runId, attempt: row.attempt, outcome: row.outcome, complete: row.complete, startedAt: row.startedAt,
      finishedAt: row.finishedAt, archivedAt: row.finishedAt, counters, cumulativeCounters: counters,
      spend: spend(620, 140), cumulativeSpend: spend(620, 140), errorCodes: [], ledgerMonths: ['2026-08'],
      operationIds: ['op-1', 'op-2', 'op-3'], quality: row.quality,
    },
    operations: [
      { operationId: 'op-1', ordinal: 0, attempt: row.attempt, unitId: 'program', path: subjectPath, level: 'file', state: 'done', startedAt: row.startedAt, finishedAt: row.finishedAt, providerRunId: 'provider-evidence', reviewedHash: hash('c'), grade: { score: row.quality?.lowestGrade ?? 84, band: row.quality?.lowestBand ?? 'B' } },
      { operationId: 'op-2', ordinal: 1, attempt: row.attempt, unitId: 'jobs', path: 'src/QualityStudio.Api/ReviewJobs.cs', level: 'file', state: 'done', startedAt: row.startedAt, finishedAt: row.finishedAt, grade: { score: 91, band: 'A' } },
      { operationId: 'op-3', ordinal: 2, attempt: row.attempt, unitId: 'archive', path: 'src/QualityStudio.Api/ReviewRunArchiveStore.cs', level: 'file', state: 'done', startedAt: row.startedAt, finishedAt: row.finishedAt, verdict: { type: 'security', value: 'clean' } },
    ],
    findings: [
      { operationId: 'op-1', fingerprint: hash('b'), findingId: 'error-boundary', ruleId: 'correctness.error-boundary', severity: 'high', title: 'Stored report failures need a precise client boundary', locations: [{ path: subjectPath, startLine: 112, startColumn: 9, endLine: 113, endColumn: 40 }], state: 'open' },
      { operationId: 'op-2', fingerprint: hash('e'), findingId: 'cursor-bound', ruleId: 'correctness.cursor-bound', severity: 'medium', title: 'History cursor should reject out-of-range partitions', locations: [{ path: 'src/QualityStudio.Api/ReviewJobs.cs', startLine: 88, startColumn: 5, endLine: 88, endColumn: 60 }], state: 'open' },
    ],
  };
};

const usage = (input, out) => ({ ...tokens(input, out), cost: null, currency: null, priceStatus: 'unavailable' });
const diffResponse = {
  beforeRunId: 'run-2026-08-23-a', beforeAttempt: 2, afterRunId: 'run-2026-08-24-b', afterAttempt: 1,
  comparability: { labels: ['exact'], renameCorrelation: 'none' },
  scope: { added: [{ path: 'src/QualityStudio.Api/ReviewRunDiff.cs' }], removed: [], persisting: [{ path: subjectPath }], changedHashes: [{ path: subjectPath }] },
  inputs: [{ unitId: 'program', path: subjectPath, beforeHash: hash('c'), afterHash: hash('f') }],
  execution: { before: { outcome: 'done', complete: true, failedFiles: 0, skippedFiles: 0, durationMs: 11000 }, after: { outcome: 'done', complete: true, failedFiles: 0, skippedFiles: 0, durationMs: 8000 }, failedFilesChange: 0, skippedFilesChange: 0, durationMsChange: -3000 },
  grades: [
    { unitId: 'program', unitPath: subjectPath, kind: 'code', before: { score: 76, band: 'C' }, after: { score: 84, band: 'B' }, scoreChange: 8, regression: false },
    { unitId: 'jobs', unitPath: 'src/QualityStudio.Api/ReviewJobs.cs', kind: 'code', before: { score: 94, band: 'A' }, after: { score: 91, band: 'A' }, scoreChange: -3, regression: true },
  ],
  verdicts: [{ unitId: 'archive', path: 'src/QualityStudio.Api/ReviewRunArchiveStore.cs', type: 'security', before: 'needs-review', after: 'clean' }],
  findings: {
    new: [{ identity: hash('e'), unitPath: 'src/QualityStudio.Api/ReviewJobs.cs', severity: 'medium', title: 'History cursor should reject out-of-range partitions' }],
    resolved: [{ identity: hash('g'), unitPath: subjectPath, severity: 'low', title: 'Redundant cancellation token forward' }, { identity: hash('h'), unitPath: subjectPath, severity: 'medium', title: 'Ledger month parsing ignores culture' }],
    persisting: [{ identity: hash('b'), unitPath: subjectPath, severity: 'high', title: 'Stored report failures need a precise client boundary' }],
  },
  findingChanges: [{ identity: hash('b'), beforeSeverity: 'critical', afterSeverity: 'high', beforeState: 'open', afterState: 'open', renamed: false }],
  economy: { before: usage(910, 240), after: usage(620, 140), inputTokensChange: -290, outputTokensChange: -100, cachedInputTokensChange: 0, reasoningOutputTokensChange: 0, durationMsChange: -3000, costChange: null },
};

const run = {
  id: 'review-evidence-20260824', repositoryId: 'default', path: subjectPath, level: 'file', kind: 'code',
  model: 'gpt-evidence', thinkingLevel: 'high', cliType: 'codex', state: 'done', totalFiles: 1, completedFiles: 1,
  failedFiles: 0, skippedFiles: 0, createdAt: '2026-08-24T09:10:00Z', startedAt: '2026-08-24T09:10:01Z',
  finishedAt: '2026-08-24T09:10:09Z', files: [{ path: subjectPath, state: 'done', startedAt: '2026-08-24T09:10:01Z', finishedAt: '2026-08-24T09:10:09Z', error: null }],
  errors: [], usageOperations: 1, usage: tokens(620, 140), estimate: null, tokenCap: null, costCap: null, costSpent: null,
  currency: null, priceStatus: 'unavailable', aggregateState: null, stopReason: null, deviation: null,
};
const treeNode = { id: 'program', name: 'Program.cs', path: subjectPath, level: 'file', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 84, band: 'B', metaPath: 'src/QualityStudio.Api/.quality/reviews/files/Program.cs.code.review-meta.json' } }, children: [] };
const usageReport = { generatedAt: run.finishedAt, runs: 3, inputTokens: 2040, outputTokens: 500, cachedInputTokens: 120, reasoningOutputTokens: 60, durationMs: 12400, byModel: [{ key: 'gpt-evidence', runs: 3, inputTokens: 2040, outputTokens: 500, cachedInputTokens: 120, reasoningOutputTokens: 60, durationMs: 12400 }], byKind: [], byDay: [], byReviewRun: [], recent: [] };

async function fulfillApi(route) {
  const url = new URL(route.request().url());
  const path = url.pathname;
  let body;
  if (path.endsWith('/review/history')) {
    const kind = url.searchParams.get('kind');
    const outcome = url.searchParams.get('outcome');
    const exactPath = url.searchParams.get('path');
    body = {
      runs: historyRuns.filter(row =>
        (!kind || row.kind === kind) && (!outcome || row.outcome === outcome) && (!exactPath || row.path === exactPath)),
      nextCursor: null,
    };
  } else if (/\/review\/history\/[^/]+\/diff$/.test(path)) body = diffResponse;
  else if (/\/review\/history\/[^/]+$/.test(path)) body = detailFor(decodeURIComponent(path.split('/').pop()));
  else if (path === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (path === '/api/models') body = { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-08-24', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] };
  else if (path.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, children: [treeNode] }] };
  else if (path.endsWith('/file')) body = { path: subjectPath, content: 'var builder = WebApplication.CreateBuilder(args);\n', metaDocuments: [], sizeBytes: 50, lineEnding: 'lf', encoding: 'utf-8' };
  else if (path.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (path.endsWith('/inputs')) body = { kinds: {} };
  else if (path.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (path.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (path.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (path.endsWith('/review/runs/trend')) body = { points: [], nextCursor: null };
  else if (path.endsWith('/review/runs')) body = { runs: [run] };
  else if (path.endsWith('/usage')) body = usageReport;
  else if (path === '/api/quotas') body = { at: run.finishedAt, ttlSeconds: 600, providers: [] };
  else if (path.endsWith('/project')) body = { generatedAt: run.finishedAt, grades: [], findings: { open: 0, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path: '.' }, reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 1, folderCount: 1, bytes: 50, lines: 1, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
  else body = {};
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

// `before` runs against origin/main, where the History section does not exist yet;
// the remaining captures only apply to the delivered branch.
const stage = process.env.QS_STAGE ?? 'after';
const evidence = [];

async function openRunDrawer(page, theme) {
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', subjectPath);
  await page.goto(url.toString());
  await page.locator('.run-history-trigger').waitFor();
  await page.locator('.run-history-trigger').click();
  await page.locator('.run-history-drawer').waitFor();
}

async function capture(page, fileName) {
  await page.locator('.run-history-drawer').screenshot({ path: join(output, fileName) });
  return fileName;
}

if (stage === 'before') {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1200 }, reducedMotion: 'reduce' });
  await page.route('**/api/**', fulfillApi);
  await openRunDrawer(page, 'dark');
  await page.waitForTimeout(1500);
  evidence.push({ name: 'before', fileName: await capture(page, 'qs-85-history-before.png'), historySection: await page.locator('.history').count() });
  await page.close();
} else {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1200 }, reducedMotion: 'reduce' });
  await page.route('**/api/**', fulfillApi);
  await openRunDrawer(page, 'dark');
  // The history block is `@defer (on idle)`, so wait for hydration rather than a fixed delay.
  await page.locator('.history-entry').waitFor();
  evidence.push({ name: 'after-collapsed', fileName: await capture(page, 'qs-85-history-after-collapsed.png'), historySection: await page.locator('.history').count() });

  await page.locator('.history-entry').click();
  await page.locator('.history-row').first().waitFor();
  const rowCount = await page.locator('.history-row').count();
  const corruptCount = await page.locator('.history-row.corrupt').count();
  evidence.push({ name: 'after-expanded', fileName: await capture(page, 'qs-85-history-after-expanded.png'), rows: rowCount, corruptRows: corruptCount });

  await page.locator('.history-row .history-main').first().click();
  await page.locator('.history-drawer').waitFor();
  evidence.push({ name: 'after-detail', fileName: await capture(page, 'qs-85-history-after-detail.png'), operations: await page.locator('.history-drawer .operation').count(), url: new URL(page.url()).search });

  await page.locator('.history-drawer .quiet').click();
  await page.locator('.history-row').nth(0).locator('.compare-toggle').click();
  await page.locator('.history-row').nth(1).locator('.compare-toggle').click();
  await page.locator('.history-diff').waitFor();
  evidence.push({ name: 'after-compare', fileName: await capture(page, 'qs-85-history-after-compare.png'), gradeDeltas: await page.locator('.grade-delta').count(), regressions: await page.locator('.grade-delta.regression').count(), url: new URL(page.url()).search });

  // Degraded rows must stay visible but must not be selectable for comparison.
  evidence.push({
    name: 'guards',
    corruptSelectable: await page.locator('.history-row.corrupt .compare-toggle').isEnabled(),
    legacySelectable: await page.locator('.history-row').last().locator('.compare-toggle').isEnabled(),
  });
  await page.close();
}

await browser.close();
const file = stage === 'before' ? 'qs-85-history-before.json' : 'qs-85-history-evidence.json';
await writeFile(join(output, file), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, stage, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, stage, evidence }, null, 2));
