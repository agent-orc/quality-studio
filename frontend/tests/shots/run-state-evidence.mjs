import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

// QS-100: a run whose files partly or wholly failed used to report state "done" — the run row
// and run history made a failed review look successful. This captures the run history drawer,
// with a run's canonical detail open, for each of the three terminal outcomes the fix now
// distinguishes: all files succeeded ("done"), some files failed but the run finished
// ("partial"), and the run itself errored out ("failed"). A run with failures never renders
// as plain "done" in any of the three captures.
const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const hash = value => `sha256:${value.repeat(64).slice(0, 64)}`;
const path = 'backend/QualityStudio.Api/Program.cs';

const baseRun = {
  repositoryId: 'default', path, level: 'file', kind: 'code',
  model: 'gpt-evidence', thinkingLevel: 'high', cliType: 'codex', totalFiles: 1,
  skippedFiles: 0, createdAt: '2026-09-15T08:00:00Z', startedAt: '2026-09-15T08:00:01Z',
  finishedAt: '2026-09-15T08:00:05Z', errors: [], usageOperations: 1,
  usage: { inputTokens: 620, outputTokens: 140, cachedInputTokens: 80, reasoningOutputTokens: 30, durationMs: 4000 },
  estimate: null, tokenCap: null, costCap: null, costSpent: null, currency: null, priceStatus: 'unavailable',
  aggregateState: null, stopReason: null, deviation: null,
};

const cases = {
  done: {
    id: 'qs-100-done', state: 'done', completedFiles: 1, failedFiles: 0,
    files: [{ path, state: 'done', startedAt: baseRun.startedAt, finishedAt: baseRun.finishedAt, error: null }],
    completeness: 'complete', outcome: 'done', partialReason: null,
  },
  partial: {
    id: 'qs-100-partial', state: 'partial', completedFiles: 1, failedFiles: 1,
    files: [{ path, state: 'failed', startedAt: baseRun.startedAt, finishedAt: baseRun.finishedAt, error: 'The reviewer process exited non-zero.' }],
    completeness: 'partial', outcome: 'failed', partialReason: '1 unit(s) failed.',
  },
  failed: {
    id: 'qs-100-failed', state: 'failed', completedFiles: 0, failedFiles: 0,
    files: [{ path, state: 'queued', startedAt: null, finishedAt: null, error: null }],
    completeness: 'partial', outcome: 'failed', partialReason: 'Run ended in state failed.',
    errors: ['The review queue is unavailable.'],
  },
};

const treeNode = { id: 'program', name: 'Program.cs', path, level: 'file', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 88, band: 'B', metaPath: 'backend/QualityStudio.Api/.quality/reviews/files/Program.cs.code.review-meta.json' } }, children: [] };

async function fulfillApi(runId, run, report, trend, route) {
  const url = new URL(route.request().url());
  const p = url.pathname;
  let body;
  if (p === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (p === '/api/models') body = { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-09-15', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] };
  else if (p.endsWith('/tree/v2')) body = { schemaVersion: 2, parentId: null, path: '.', offset: 0, limit: 500, nextCursor: null, nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, hasChildren: true, childCount: 1, children: [treeNode] }] };
  else if (p.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, children: [treeNode] }] };
  else if (p.endsWith('/file')) body = { path: run.path, content: 'var builder = WebApplication.CreateBuilder(args);\n', metaDocuments: [], sizeBytes: 50, lineEnding: 'lf', encoding: 'utf-8' };
  else if (p.endsWith('/scan')) body = { files: [], freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (p.endsWith('/inputs')) body = { kinds: {} };
  else if (p.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (p.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (p.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (p.endsWith('/review/runs/trend')) body = trend;
  else if (p.endsWith(`/${runId}/report`)) body = report;
  else if (p.endsWith('/review/runs')) body = { runs: [run] };
  else if (p.endsWith('/usage')) body = { generatedAt: run.finishedAt, runs: 1, inputTokens: 620, outputTokens: 140, cachedInputTokens: 80, reasoningOutputTokens: 30, durationMs: 4000, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] };
  else if (p === '/api/quotas') body = { at: run.finishedAt, ttlSeconds: 600, providers: [] };
  else if (p.endsWith('/project')) body = { generatedAt: run.finishedAt, grades: [], findings: { open: 0, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 0, stale: 0, missing: 0, total: 0, path: '.' }, reviewCoverage: { reviewedFiles: 0, totalFiles: 0, percent: 0, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 0, folderCount: 0, bytes: 0, lines: 0, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
  else body = {};
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

const evidence = [];
for (const [name, fixture] of Object.entries(cases)) {
  const run = { ...baseRun, ...fixture, errors: fixture.errors ?? baseRun.errors };
  const report = {
    $schema: 'https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json', schemaVersion: 1,
    run: { ...run, revision: 1, repositoryName: 'Quality Studio', scopeUnitId: 'program', completeness: fixture.completeness, force: false },
    subject: { manifestHash: hash('a'), targets: [{ unitId: 'program', name: 'Program.cs', path, subjectHash: hash('c') }] },
    execution: { reviewed: run.completedFiles, reusedFresh: 0, failed: run.failedFiles, skipped: 0, cancelled: 0, aggregateOutcome: null, errors: run.errors,
      usage: { ...run.usage, operations: 1, cost: null, currency: null, priceStatus: 'unavailable', inputEstimateDeviationPercent: null, outputEstimateDeviationPercent: null, costEstimateDeviationPercent: null },
      cap: { tokenLimit: null, costLimit: null, outcome: 'not-configured', reason: null }, estimate: null },
    observations: [{ unitId: 'program', level: 'file', path, outcome: fixture.outcome, producedByRun: fixture.outcome === 'done',
      sidecarPath: fixture.outcome === 'done' ? 'backend/QualityStudio.Api/.quality/reviews/files/Program.cs.code.review-meta.json' : null,
      sidecarSha256: fixture.outcome === 'done' ? hash('d') : null,
      capturedAt: run.finishedAt, reviewedHash: fixture.outcome === 'done' ? hash('c') : null, providerRunId: 'provider-evidence',
      grade: fixture.outcome === 'done' ? { score: 88, band: 'B', rationale: 'Portable evidence is complete.' } : null,
      summary: fixture.outcome === 'done' ? 'The run completed with no active findings.' : null, findings: [] }],
    delta: { status: 'unavailable', priorRunId: null, reason: 'Partial runs are not comparable.', new: [], persisting: [], resolved: [], stateChanged: [] },
    summary: { score: fixture.outcome === 'done' ? 88 : null, grade: fixture.outcome === 'done' ? 'B' : null, findings: { total: 0, bySeverity: { critical: 0, high: 0, medium: 0, low: 0, info: 0 }, byState: { open: 0, accepted: 0, waived: 0, 'false-positive': 0, resolved: 0 } }, highestSeverity: null, partialReason: fixture.partialReason },
  };
  const trend = { points: [{ runId: run.id, revision: 1, finishedAt: run.finishedAt, state: run.state, completeness: fixture.completeness, comparable: fixture.outcome === 'done', comparisonReason: fixture.outcome === 'done' ? null : 'Partial runs are not comparable.', score: report.summary.score, grade: report.summary.grade, activeFindings: 0, newFindings: 0, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: run.completedFiles, reusedFresh: 0, failed: run.failedFiles, skipped: 0, inputTokens: run.usage.inputTokens, outputTokens: run.usage.outputTokens, cost: null, currency: null }], nextCursor: null };

  const page = await browser.newPage({ viewport: { width: 1440, height: 960 }, reducedMotion: 'reduce' });
  await page.route('**/api/**', route => fulfillApi(run.id, run, report, trend, route));
  const url = new URL(baseUrl);
  url.searchParams.set('theme', 'light');
  url.searchParams.set('path', path);
  await page.goto(url.toString());
  await page.locator('.run-history-trigger').waitFor();
  await page.locator('.run-history-trigger').click();
  const open = page.locator('.run-open').first();
  await open.waitFor();
  await open.click();
  await page.locator('.run-detail-surface').waitFor();
  const fileName = `qs-100-run-report-${name}.png`;
  await page.screenshot({ path: join(output, fileName), fullPage: true });
  const badge = page.locator('.run-row .run-heading .studio-badge').first();
  evidence.push({
    name,
    fileName,
    visibleStateLabel: await badge.textContent(),
    badgeTone: await badge.getAttribute('data-tone'),
    failedFiles: run.failedFiles,
  });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-100-run-state-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
