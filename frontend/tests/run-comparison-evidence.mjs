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
  id: 'review-candidate-20260812', repositoryId: 'default', path: 'src/QualityStudio.Api/Program.cs', level: 'file', kind: 'code',
  model: 'gpt-candidate', thinkingLevel: 'high', cliType: 'codex', state: 'done', totalFiles: 1, completedFiles: 1,
  failedFiles: 0, skippedFiles: 0, createdAt: '2026-08-12T00:00:00Z', startedAt: '2026-08-12T00:00:01Z',
  finishedAt: '2026-08-12T00:08:12Z', files: [{ path: 'src/QualityStudio.Api/Program.cs', state: 'done', startedAt: '2026-08-12T00:00:01Z', finishedAt: '2026-08-12T00:08:12Z', error: null }],
  errors: [], usageOperations: 1, usage: { inputTokens: 62000, outputTokens: 13000, cachedInputTokens: 8000, reasoningOutputTokens: 3000, durationMs: 491000 },
  estimate: null, tokenCap: null, costCap: null, costSpent: null, currency: null, priceStatus: 'unavailable', aggregateState: null,
  stopReason: null, deviation: null,
};
const finding = (key, title, state, line, severity = 'high') => ({
  id: key, ruleId: `correctness.${key}`, aspect: 'correctness', severity, state, title,
  description: `${title} is preserved in the immutable run outcome.`, recommendation: 'Review the recorded evidence.',
  evidence: 'Captured from the terminal run snapshot.', fingerprint: hash(key),
  locations: [{ path: run.path, startLine: line, startColumn: 9, endLine: line + 1, endColumn: 40 }],
  source: 'agent', sensorId: null, producer: null,
});
const nextFinding = finding('new-boundary', 'New report boundary needs a typed failure', 'open', 112);
const stableFinding = finding('stable-route', 'Route evidence remains stable', 'open', 128, 'low');
const dispositionFinding = finding('disposition', 'Operator disposition changed', 'accepted', 142, 'medium');
const resolvedFinding = finding('resolved-cap', 'Cap accounting obscured the final outcome', 'open', 168, 'medium');

const identity = (id, model, finishedAt) => ({
  ...run, id, revision: 1, repositoryName: 'Quality Studio', scopeUnitId: 'program', completeness: 'complete',
  model, finishedAt, force: false,
});
const execution = (inputTokens, outputTokens, durationMs) => ({
  reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, cancelled: 0, aggregateOutcome: null, errors: [],
  usage: { inputTokens, outputTokens, cachedInputTokens: 8000, reasoningOutputTokens: 3000, durationMs, operations: 1,
    cost: null, currency: null, priceStatus: 'unavailable', inputEstimateDeviationPercent: null,
    outputEstimateDeviationPercent: null, costEstimateDeviationPercent: null },
  cap: { tokenLimit: null, costLimit: null, outcome: 'not-configured', reason: null }, estimate: null,
});
const observation = (findings, score, band, subjectHash) => ({
  unitId: 'program', level: 'file', path: run.path, outcome: 'done', producedByRun: true,
  sidecarPath: 'src/QualityStudio.Api/.quality/reviews/files/Program.cs.code.review-meta.json', sidecarSha256: hash(subjectHash),
  capturedAt: run.finishedAt, reviewedHash: hash(subjectHash), providerRunId: `provider-${subjectHash}`,
  grade: { score, band, rationale: 'The canonical evidence is complete.' }, summary: 'The run completed with traceable findings.', findings,
});
const baseline = {
  $schema: 'https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json', schemaVersion: 1,
  run: identity('review-baseline-20260810', 'gpt-baseline', '2026-08-10T00:09:14Z'),
  subject: { manifestHash: hash('baseline-inputs'), targets: [{ unitId: 'program', name: 'Program.cs', path: run.path, subjectHash: hash('baseline-subject') }] },
  execution: execution(53000, 11000, 554000),
  observations: [observation([stableFinding, { ...dispositionFinding, state: 'open' }, resolvedFinding], 72, 'C', 'baseline-sidecar')],
  delta: { status: 'unavailable', priorRunId: null, reason: 'No prior comparable run snapshot exists.', new: [], persisting: [], resolved: [], stateChanged: [] },
  summary: { score: 72, grade: 'C', findings: { total: 3, bySeverity: {}, byState: {} }, highestSeverity: 'medium', partialReason: null },
};
const report = {
  $schema: baseline.$schema, schemaVersion: 1,
  run: identity(run.id, run.model, run.finishedAt),
  subject: { manifestHash: hash('candidate-inputs'), targets: [{ unitId: 'program', name: 'Program.cs', path: run.path, subjectHash: hash('candidate-subject') }] },
  execution: execution(62000, 13000, 491000),
  observations: [observation([nextFinding, stableFinding, dispositionFinding], 84, 'B', 'candidate-sidecar')],
  delta: { status: 'available', priorRunId: baseline.run.id, reason: null, new: [nextFinding.fingerprint],
    persisting: [stableFinding.fingerprint, dispositionFinding.fingerprint], resolved: [resolvedFinding.fingerprint],
    stateChanged: [dispositionFinding.fingerprint] },
  summary: { score: 84, grade: 'B', findings: { total: 3, bySeverity: {}, byState: {} }, highestSeverity: 'high', partialReason: null },
};
const trend = { points: [
  { runId: run.id, revision: 1, finishedAt: run.finishedAt, state: 'done', completeness: 'complete', comparable: true, comparisonReason: null, score: 84, grade: 'B', activeFindings: 3, newFindings: 1, persistingFindings: 1, resolvedFindings: 1, stateChangedFindings: 1, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 62000, outputTokens: 13000, cost: null, currency: null },
  { runId: baseline.run.id, revision: 1, finishedAt: baseline.run.finishedAt, state: 'done', completeness: 'complete', comparable: true, comparisonReason: null, score: 72, grade: 'C', activeFindings: 3, newFindings: 0, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 53000, outputTokens: 11000, cost: null, currency: null },
], nextCursor: null };

const treeNode = { id: 'program', name: 'Program.cs', path: run.path, level: 'file', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 84, band: 'B', metaPath: report.observations[0].sidecarPath } }, children: [] };
const metaFinding = { ...nextFinding, locations: [{ path: run.path, range: { start: { line: 112, column: 9 }, end: { line: 113, column: 40 } } }] };
const meta = { reviewedAt: run.finishedAt, kind: 'code', reviewer: { agent: 'quality-reviewer', model: run.model }, grade: report.observations[0].grade, summary: report.observations[0].summary, findings: [metaFinding] };
const emptyUsage = { generatedAt: run.finishedAt, runs: 1, inputTokens: 62000, outputTokens: 13000, cachedInputTokens: 8000, reasoningOutputTokens: 3000, durationMs: 491000, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] };

async function fulfillApi(route) {
  const path = new URL(route.request().url()).pathname;
  let body;
  if (path === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (path === '/api/models') body = { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-08-12', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] };
  else if (path.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, children: [treeNode] }] };
  else if (path.endsWith('/file')) body = { path: run.path, content: 'var builder = WebApplication.CreateBuilder(args);\n', metaDocuments: [meta], sizeBytes: 50, lineEnding: 'lf', encoding: 'utf-8' };
  else if (path.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (path.endsWith('/inputs')) body = { kinds: {} };
  else if (path.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (path.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (path.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (path.endsWith('/review/runs/trend')) body = trend;
  else if (path.endsWith(`/${baseline.run.id}/report`)) body = baseline;
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
  await page.locator('.run-history-trigger').click();
  await page.locator('.run-open').first().click();
  const compare = page.locator('.run-compare-trigger');
  await compare.focus();
  await page.keyboard.press('Enter');
  const comparison = page.locator('.run-comparison');
  await comparison.waitFor();
  await comparison.scrollIntoViewIfNeeded();
  const fileName = `qs-83-run-comparison-${capture.name}.png`;
  await page.screenshot({ path: join(output, fileName), fullPage: true });
  evidence.push({
    ...capture,
    fileName,
    keyboardOpened: await comparison.isVisible(),
    warning: await page.locator('.comparison-warning').textContent(),
    findingDeltaRows: await page.locator('.comparison-findings article').count(),
    comparisonText: (await comparison.textContent()).replace(/\s+/g, ' ').trim(),
  });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-83-run-comparison-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
