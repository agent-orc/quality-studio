import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? (process.env.JOB_RESULTS_DIR ? join(process.env.JOB_RESULTS_DIR, 'after') : 'evidence/after'));
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const hash = value => `sha256:${value.repeat(64).slice(0, 64)}`;
const run = (id, finishedAt, model, thinkingLevel, score) => ({
  id, repositoryId: 'default', path: 'src/Billing/InvoiceService.cs', level: 'file', kind: 'code',
  model, thinkingLevel, cliType: 'codex', state: 'done', totalFiles: 1, completedFiles: 1, failedFiles: 0,
  skippedFiles: 0, createdAt: finishedAt, startedAt: finishedAt, finishedAt,
  files: [{ path: 'src/Billing/InvoiceService.cs', state: 'done', startedAt: finishedAt, finishedAt, error: null }],
  errors: [], usageOperations: 1, usage: { inputTokens: score * 10, outputTokens: score * 2, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: score * 100 },
  estimate: null, tokenCap: null, costCap: null, costSpent: score / 1000, currency: 'USD', priceStatus: 'priced',
  aggregateState: null, stopReason: null, deviation: null,
});
const candidate = run('review-candidate-191', '2026-08-11T08:00:00Z', 'gpt-5.6-sol', 'high', 84);
const baseline = run('review-baseline-184', '2026-08-08T08:00:00Z', 'gpt-5.5-terra', 'medium', 72);
const location = path => [{ path, startLine: 58, startColumn: 5, endLine: 59, endColumn: 60 }];
const comparisonFindings = [
  { fingerprint: hash('n'), change: 'new', title: 'Cancellation is not propagated', severity: 'high', baselineState: null, candidateState: 'open', locations: location(candidate.path) },
  { fingerprint: hash('d'), change: 'disposition-changed', title: 'Tax timeout is intentional for this boundary', severity: 'medium', baselineState: 'open', candidateState: 'waived', locations: location(candidate.path) },
  { fingerprint: hash('r'), change: 'resolved', title: 'Synchronous tax call in async flow', severity: 'high', baselineState: 'accepted', candidateState: null, locations: location(candidate.path) },
  { fingerprint: hash('u'), change: 'unchanged', title: 'Retry policy can duplicate invoice writes', severity: 'medium', baselineState: 'open', candidateState: 'open', locations: location(candidate.path) },
];
const comparison = {
  baseline: { runId: baseline.id, revision: 1, finishedAt: baseline.finishedAt, score: 72, grade: 'C', activeFindings: 3,
    activeBySeverity: { critical: 0, high: 2, medium: 1, low: 0, info: 0 }, reviewed: 42, reusedFresh: 0, failed: 0, skipped: 0,
    cliType: 'codex', model: baseline.model, thinkingLevel: baseline.thinkingLevel, subjectManifestHash: hash('b'), reviewInputsHash: hash('i'),
    durationMs: 554000, inputTokens: 52000, outputTokens: 12000, cost: 1.18, currency: 'USD' },
  candidate: { runId: candidate.id, revision: 1, finishedAt: candidate.finishedAt, score: 84, grade: 'B', activeFindings: 2,
    activeBySeverity: { critical: 0, high: 1, medium: 1, low: 0, info: 0 }, reviewed: 42, reusedFresh: 0, failed: 0, skipped: 0,
    cliType: 'codex', model: candidate.model, thinkingLevel: candidate.thinkingLevel, subjectManifestHash: hash('c'), reviewInputsHash: hash('j'),
    durationMs: 491000, inputTokens: 61000, outputTokens: 14000, cost: 1.42, currency: 'USD' },
  subjectChanged: true, reviewInputsChanged: true, routeChanged: true,
  interpretation: 'Route, source subject, or review inputs changed. Compare outcomes only; do not attribute the delta to the model.',
  counts: { new: 1, unchanged: 1, resolved: 1, dispositionChanged: 1 }, findings: comparisonFindings,
};
const report = {
  $schema: 'https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json', schemaVersion: 1,
  run: { ...candidate, revision: 1, repositoryName: 'Quality Studio', scopeUnitId: 'billing-service', completeness: 'complete', force: false },
  subject: { manifestHash: comparison.candidate.subjectManifestHash, targets: [{ unitId: 'billing-service', name: 'InvoiceService.cs', path: candidate.path, subjectHash: hash('s') }] },
  execution: { reviewed: 42, reusedFresh: 0, failed: 0, skipped: 0, cancelled: 0, aggregateOutcome: null, errors: [],
    usage: { ...candidate.usage, operations: 42, cost: 1.42, currency: 'USD', priceStatus: 'priced', inputEstimateDeviationPercent: null, outputEstimateDeviationPercent: null, costEstimateDeviationPercent: null },
    cap: { tokenLimit: null, costLimit: null, outcome: 'not-configured', reason: null }, estimate: null },
  observations: [], delta: { status: 'available', priorRunId: baseline.id, reason: null, new: [hash('n')], persisting: [hash('u')], resolved: [hash('r')], stateChanged: [hash('d')] },
  summary: { score: 84, grade: 'B', findings: { total: 2, bySeverity: comparison.candidate.activeBySeverity, byState: {} }, highestSeverity: 'high', partialReason: null },
};
const trend = { points: [
  { runId: candidate.id, revision: 1, finishedAt: candidate.finishedAt, state: 'done', completeness: 'complete', comparable: true, comparisonReason: null, score: 84, grade: 'B', activeFindings: 2, newFindings: 1, persistingFindings: 1, resolvedFindings: 1, stateChangedFindings: 1, reviewed: 42, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 61000, outputTokens: 14000, cost: 1.42, currency: 'USD' },
  { runId: baseline.id, revision: 1, finishedAt: baseline.finishedAt, state: 'done', completeness: 'complete', comparable: true, comparisonReason: null, score: 72, grade: 'C', activeFindings: 3, newFindings: 0, persistingFindings: 3, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 42, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 52000, outputTokens: 12000, cost: 1.18, currency: 'USD' },
], nextCursor: null };
const metaFinding = { id: 'current', fingerprint: hash('n'), ruleId: 'correctness.cancellation', aspect: 'correctness', severity: 'high', state: 'open', title: comparisonFindings[0].title, description: 'The cancellation token stops before the remote boundary.', recommendation: 'Pass the token to the tax client.', locations: [{ path: candidate.path, range: { start: { line: 58, column: 5 }, end: { line: 59, column: 60 } } }] };
const treeNode = { id: 'billing-service', name: 'InvoiceService.cs', path: candidate.path, level: 'file', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 84, band: 'B', metaPath: '.quality/reviews/invoice.json' } }, children: [] };
const emptyUsage = { generatedAt: candidate.finishedAt, runs: 2, inputTokens: 113000, outputTokens: 26000, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 1045000, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] };

async function fulfillApi(route) {
  const url = new URL(route.request().url());
  const path = url.pathname;
  let body;
  if (path === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (path === '/api/models') body = { schemaVersion: 1, policyVersion: '2026-08-11', evidenceAsOfDate: '2026-08-11', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['medium', 'high'], models: [] };
  else if (path.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, children: [treeNode] }] };
  else if (path.endsWith('/file')) body = { path: candidate.path, content: 'public async Task CreateInvoiceAsync() { }\n', metaDocuments: [{ reviewedAt: candidate.finishedAt, kind: 'code', reviewer: { agent: 'quality-reviewer', model: candidate.model }, grade: { score: 84, band: 'B', rationale: 'Quality improved with one new boundary finding.' }, summary: 'One active finding remains.', findings: [metaFinding] }], sizeBytes: 48, lineEnding: 'lf', encoding: 'utf-8' };
  else if (path.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (path.endsWith('/inputs')) body = { kinds: {} };
  else if (path.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (path.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (path.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (path.endsWith('/review/runs/compare')) body = comparison;
  else if (path.endsWith('/review/runs/trend')) body = trend;
  else if (path.endsWith(`/${candidate.id}/report`)) body = report;
  else if (path.endsWith('/review/runs')) body = { runs: [candidate, baseline] };
  else if (path.endsWith('/usage')) body = emptyUsage;
  else if (path === '/api/quotas') body = { at: candidate.finishedAt, ttlSeconds: 600, providers: [] };
  else if (path.endsWith('/project')) body = { generatedAt: candidate.finishedAt, grades: [], findings: { open: 1, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path: '.' }, reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 1, folderCount: 1, bytes: 48, lines: 1, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
  else body = {};
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

const evidence = [];
for (const capture of [
  { name: 'dark', theme: 'dark', viewport: { width: 1440, height: 960 } },
  { name: 'light', theme: 'light', viewport: { width: 1440, height: 960 } },
  { name: 'narrow', theme: 'dark', viewport: { width: 960, height: 900 } },
]) {
  const page = await browser.newPage({ viewport: capture.viewport, reducedMotion: 'reduce' });
  await page.route('**/api/**', fulfillApi);
  const url = new URL(baseUrl);
  url.searchParams.set('theme', capture.theme);
  url.searchParams.set('path', candidate.path);
  await page.goto(url.toString());
  await page.locator('.run-history-trigger').click();
  await page.locator('.run-open').first().click();
  const compare = page.locator('.run-compare-trigger');
  await compare.waitFor();
  await compare.focus();
  await page.keyboard.press('Enter');
  await page.locator('.comparison-workbench').waitFor();
  await page.locator('.comparison-finding').first().waitFor();
  const audit = await page.evaluate(() => ({
    selectorCount: document.querySelectorAll('.comparison-controls select').length,
    findingDeltaCount: document.querySelectorAll('.comparison-finding').length,
    interpretation: document.querySelector('.comparison-warning')?.textContent?.trim(),
    modal: document.querySelector('.comparison-workbench')?.getAttribute('aria-modal'),
  }));
  if (audit.selectorCount !== 2 || audit.findingDeltaCount !== 4 || audit.modal !== 'true' || !audit.interpretation?.includes('do not attribute')) throw new Error(`${capture.name}: comparison audit failed`);
  const fileName = `qs-83-run-comparison-${capture.name}.png`;
  await page.screenshot({ path: join(output, fileName), fullPage: true });
  evidence.push({ ...capture, fileName, keyboardOpened: true, audit });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-83-run-comparison-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
