import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const hash = value => `sha256:${value.repeat(64).slice(0, 64)}`;
const run = {
  id: 'review-evidence-20260811', repositoryId: 'default', path: 'src/QualityStudio.Api/Program.cs', level: 'file', kind: 'code',
  model: 'gpt-evidence', thinkingLevel: 'high', cliType: 'codex', state: 'done', totalFiles: 1, completedFiles: 1,
  failedFiles: 0, skippedFiles: 0, createdAt: '2026-08-11T08:00:00Z', startedAt: '2026-08-11T08:00:01Z',
  finishedAt: '2026-08-11T08:00:05Z', files: [{ path: 'src/QualityStudio.Api/Program.cs', state: 'done', startedAt: '2026-08-11T08:00:01Z', finishedAt: '2026-08-11T08:00:05Z', error: null }],
  errors: [], usageOperations: 1, usage: { inputTokens: 620, outputTokens: 140, cachedInputTokens: 80, reasoningOutputTokens: 30, durationMs: 4000 },
  estimate: null, tokenCap: null, costCap: null, costSpent: null, currency: null, priceStatus: 'unavailable', aggregateState: null,
  stopReason: null, deviation: null,
};
const finding = {
  id: 'error-boundary', ruleId: 'correctness.error-boundary', aspect: 'correctness', severity: 'high', state: 'open',
  title: 'Stored report failures need a precise client boundary', description: 'The report is portable and keeps its exact run evidence.',
  recommendation: 'Keep report failures distinct from repository availability failures.', evidence: 'Captured from the terminal run snapshot.',
  fingerprint: hash('b'), locations: [{ path: 'src/QualityStudio.Api/Program.cs', startLine: 112, startColumn: 9, endLine: 113, endColumn: 40 }],
  source: 'agent', sensorId: null, producer: null,
};
const report = {
  $schema: 'https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json', schemaVersion: 1,
  run: { ...run, revision: 1, repositoryName: 'Quality Studio', commitSha: '59941d8f07b85ca2fd24a9ae32e491740e3bbf00', scopeUnitId: 'program', completeness: 'complete', force: false },
  subject: { manifestHash: hash('a'), targets: [{ unitId: 'program', name: 'Program.cs', path: run.path, subjectHash: hash('c') }] },
  execution: { reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, cancelled: 0, aggregateOutcome: null, errors: [],
    usage: { ...run.usage, operations: 1, cost: null, currency: null, priceStatus: 'unavailable', inputEstimateDeviationPercent: null, outputEstimateDeviationPercent: null, costEstimateDeviationPercent: null },
    cap: { tokenLimit: null, costLimit: null, outcome: 'not-configured', reason: null }, estimate: null },
  observations: [{ unitId: 'program', level: 'file', path: run.path, outcome: 'done', producedByRun: true,
    sidecarPath: 'src/QualityStudio.Api/.quality/reviews/files/Program.cs.code.review-meta.json', sidecarSha256: hash('d'),
    capturedAt: '2026-08-11T08:00:05Z', reviewedHash: hash('c'), providerRunId: 'provider-evidence',
    grade: { score: 88, band: 'B', rationale: 'Portable evidence is complete.' }, summary: 'The run completed with one actionable finding.', findings: [finding] }],
  delta: { status: 'available', priorRunId: 'review-prior', reason: null, new: [finding.fingerprint], persisting: [], resolved: [], stateChanged: [] },
  summary: { score: 88, grade: 'B', findings: { total: 1, bySeverity: { critical: 0, high: 1, medium: 0, low: 0, info: 0 }, byState: { open: 1, accepted: 0, waived: 0, 'false-positive': 0, resolved: 0 } }, highestSeverity: 'high', partialReason: null },
};
const trend = { points: [
  { runId: run.id, revision: 1, finishedAt: run.finishedAt, state: 'done', completeness: 'complete', comparable: true, comparisonReason: null, score: 88, grade: 'B', activeFindings: 1, newFindings: 1, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 620, outputTokens: 140, cost: null, currency: null },
  { runId: 'review-capped', revision: 1, finishedAt: '2026-08-10T08:00:00Z', state: 'capped', completeness: 'partial', comparable: false, comparisonReason: 'Token cap reached.', score: null, grade: null, activeFindings: 0, newFindings: 0, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 2, inputTokens: 500, outputTokens: 90, cost: null, currency: null },
], nextCursor: null };

const treeNode = { id: 'program', name: 'Program.cs', path: run.path, level: 'file', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 88, band: 'B', metaPath: report.observations[0].sidecarPath } }, children: [] };
const metaFinding = { ...finding, locations: [{ path: run.path, range: { start: { line: 112, column: 9 }, end: { line: 113, column: 40 } } }] };
const meta = { reviewedAt: run.finishedAt, kind: 'code', reviewer: { agent: 'quality-reviewer', model: run.model }, grade: report.observations[0].grade, summary: report.observations[0].summary, findings: [metaFinding] };
const emptyUsage = { generatedAt: run.finishedAt, runs: 1, inputTokens: 620, outputTokens: 140, cachedInputTokens: 80, reasoningOutputTokens: 30, durationMs: 4000, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] };

async function fulfillApi(route) {
  const url = new URL(route.request().url());
  const path = url.pathname;
  let body;
  if (path === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (path === '/api/models') body = { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-08-11', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] };
  else if (path.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, children: [treeNode] }] };
  else if (path.endsWith('/file')) body = { path: run.path, content: 'var builder = WebApplication.CreateBuilder(args);\n', metaDocuments: [meta], sizeBytes: 50, lineEnding: 'lf', encoding: 'utf-8' };
  else if (path.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (path.endsWith('/inputs')) body = { kinds: {} };
  else if (path.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (path.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (path.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (path.endsWith('/review/runs/trend')) body = trend;
  else if (path.endsWith(`/${run.id}/report`)) body = report;
  else if (path.endsWith('/review/runs')) body = { runs: [run] };
  else if (path.endsWith('/usage')) body = emptyUsage;
  else if (path === '/api/quotas') body = { at: run.finishedAt, ttlSeconds: 600, providers: [] };
  else if (path.endsWith('/project')) body = { generatedAt: run.finishedAt, grades: [], findings: { open: 0, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path: '.' }, reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 1, folderCount: 1, bytes: 50, lines: 1, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
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
  url.searchParams.set('path', run.path);
  await page.goto(url.toString());
  await page.locator('.run-history-trigger').waitFor();
  await page.locator('.run-history-trigger').click();
  const open = page.locator('.run-open').first();
  await open.focus();
  await page.keyboard.press('Enter');
  await page.locator('.run-detail-surface').waitFor();
  await page.locator('.commit-trend-note').waitFor();
  const fileName = `qs-87-run-dossier-${capture.name}.png`;
  await page.screenshot({ path: join(output, fileName), fullPage: true });
  evidence.push({
    ...capture,
    fileName,
    reportActions: await page.locator('.run-exports a').count(),
    openReportVisible: await page.getByRole('link', { name: 'Open HTML report' }).isVisible(),
    keyboardOpened: await page.locator('.run-detail-surface').isVisible(),
  });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-87-run-dossier-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
