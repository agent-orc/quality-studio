import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const hash = value => `sha256:${value.repeat(64).slice(0, 64)}`;
const path = 'src/QualityStudio.Api/Program.cs';
const usage = (inputTokens, outputTokens, durationMs) => ({ inputTokens, outputTokens, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs });
const run = (id, finishedAt, score, model, inputTokens, outputTokens, durationMs) => ({
  id, repositoryId: 'default', path, level: 'file', kind: 'code', model, thinkingLevel: 'high', cliType: 'codex',
  state: 'done', totalFiles: 1, completedFiles: 1, failedFiles: 0, skippedFiles: 0,
  createdAt: finishedAt, startedAt: finishedAt, finishedAt,
  files: [{ path, state: 'done', startedAt: finishedAt, finishedAt, error: null }], errors: [], usageOperations: 1,
  usage: usage(inputTokens, outputTokens, durationMs), estimate: null, tokenCap: null, costCap: null, costSpent: null,
  currency: null, priceStatus: 'unavailable', aggregateState: null, stopReason: null, deviation: null, score,
});
const baselineRun = run('review-baseline-20260808', '2026-08-08T08:00:00Z', 72, 'gpt-5.6-terra', 64000, 12000, 554000);
const candidateRun = run('review-candidate-20260811', '2026-08-11T08:00:00Z', 84, 'gpt-5.6-sol', 75000, 15000, 491000);
const finding = (fingerprint, title, severity, state, line) => ({
  id: `finding-${fingerprint}`, ruleId: `quality.${fingerprint}`, aspect: 'correctness', severity, state, title,
  description: `${title} remains traceable in the immutable run outcome.`, recommendation: 'Review the recorded evidence and source location.',
  evidence: null, fingerprint: hash(fingerprint), locations: [{ path, startLine: line, startColumn: 1, endLine: line, endColumn: 24 }],
  source: 'agent', sensorId: null, producer: null,
});
const newFinding = finding('new', 'Synchronous tax call introduced', 'high', 'open', 58);
const changedFinding = finding('changed', 'Missing cancellation propagation', 'medium', 'waived', 57);
const resolvedFinding = finding('resolved', 'Unbounded retry loop', 'high', 'open', 83);
const unchangedFinding = finding('unchanged', 'Duration log lacks route context', 'low', 'accepted', 91);

const candidateReport = {
  $schema: 'https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json', schemaVersion: 1,
  run: { ...candidateRun, revision: 1, repositoryName: 'Quality Studio', scopeUnitId: 'program', completeness: 'complete', force: false },
  subject: { manifestHash: hash('candidate'), targets: [{ unitId: 'program', name: 'Program.cs', path, subjectHash: hash('source') }] },
  execution: { reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, cancelled: 0, aggregateOutcome: null, errors: [],
    usage: { ...candidateRun.usage, operations: 1, cost: null, currency: null, priceStatus: 'unavailable', inputEstimateDeviationPercent: null, outputEstimateDeviationPercent: null, costEstimateDeviationPercent: null },
    cap: { tokenLimit: null, costLimit: null, outcome: 'not-configured', reason: null }, estimate: null },
  observations: [{ unitId: 'program', level: 'file', path, outcome: 'done', producedByRun: true, sidecarPath: 'Program.cs.review-meta.code.json', sidecarSha256: hash('sidecar'), capturedAt: candidateRun.finishedAt, reviewedHash: hash('source'), providerRunId: 'provider-candidate', grade: { score: 84, band: 'B', rationale: 'Candidate grade.' }, summary: 'Candidate outcome.', findings: [newFinding, changedFinding, unchangedFinding] }],
  delta: { status: 'available', priorRunId: baselineRun.id, reason: null, new: [newFinding.fingerprint], persisting: [changedFinding.fingerprint, unchangedFinding.fingerprint], resolved: [resolvedFinding.fingerprint], stateChanged: [changedFinding.fingerprint] },
  summary: { score: 84, grade: 'B', findings: { total: 2, bySeverity: { critical: 0, high: 1, medium: 0, low: 1, info: 0 }, byState: { open: 1, accepted: 1, waived: 1, 'false-positive': 0, resolved: 0 } }, highestSeverity: 'high', partialReason: null },
};
const snapshot = (source, score, activeFindings, manifestHash) => ({
  runId: source.id, revision: 1, finishedAt: source.finishedAt, model: source.model, thinkingLevel: source.thinkingLevel,
  cliType: source.cliType, force: false, subjectManifestHash: manifestHash, score, grade: 'B', activeFindings,
  findingsBySeverity: { critical: 0, high: activeFindings, medium: 0, low: 0, info: 0 }, reviewed: 1,
  reusedFresh: 0, failed: 0, skipped: 0, inputTokens: source.usage.inputTokens, outputTokens: source.usage.outputTokens,
  durationMs: source.usage.durationMs, cost: null, currency: null,
});
const comparison = {
  baseline: snapshot(baselineRun, 72, 3, hash('baseline')),
  candidate: snapshot(candidateRun, 84, 2, hash('candidate')),
  provenance: {
    routeChanged: true, subjectChanged: true, forceChanged: false,
    interpretation: 'Route, reviewed subject, or force mode changed. Compare the recorded outcomes, but do not attribute the delta to the model.',
    evidenceLimit: 'Canonical report v1 records reviewed subject hashes, but not a complete prompt-input hash.',
  },
  findingCounts: { new: 1, dispositionChanged: 1, resolved: 1, unchanged: 1 },
  findings: [
    { category: 'new', fingerprint: newFinding.fingerprint, baselineState: null, candidateState: 'open', finding: newFinding },
    { category: 'dispositionChanged', fingerprint: changedFinding.fingerprint, baselineState: 'open', candidateState: 'waived', finding: changedFinding },
    { category: 'resolved', fingerprint: resolvedFinding.fingerprint, baselineState: 'open', candidateState: null, finding: resolvedFinding },
    { category: 'unchanged', fingerprint: unchangedFinding.fingerprint, baselineState: 'accepted', candidateState: 'accepted', finding: unchangedFinding },
  ],
};
const trend = { points: [candidateRun, baselineRun].map(source => ({
  runId: source.id, revision: 1, finishedAt: source.finishedAt, state: 'done', completeness: 'complete', comparable: true,
  comparisonReason: null, score: source.score, grade: 'B', activeFindings: source === candidateRun ? 2 : 3,
  newFindings: source === candidateRun ? 1 : 3, persistingFindings: 0, resolvedFindings: source === candidateRun ? 1 : 0,
  stateChangedFindings: source === candidateRun ? 1 : 0, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0,
  inputTokens: source.usage.inputTokens, outputTokens: source.usage.outputTokens, cost: null, currency: null,
})), nextCursor: null };
const metaFinding = { ...newFinding, locations: [{ path, range: { start: { line: 1, column: 1 }, end: { line: 1, column: 24 } } }] };
const meta = { reviewedAt: candidateRun.finishedAt, kind: 'code', reviewer: { agent: 'quality-reviewer', model: candidateRun.model }, grade: { score: 84, band: 'B', rationale: 'Candidate grade.' }, summary: 'Candidate outcome.', findings: [metaFinding] };
const kindState = { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 84, band: 'B', metaPath: 'Program.cs.review-meta.code.json' } };

async function fulfillApi(route) {
  const url = new URL(route.request().url());
  const requestPath = url.pathname;
  let body;
  if (requestPath === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (requestPath === '/api/models') body = { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-08-12', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] };
  else if (requestPath.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: kindState, children: [{ id: 'program', name: 'Program.cs', path, level: 'file', kinds: kindState, children: [] }] }] };
  else if (requestPath.endsWith('/file')) body = { path, content: 'var builder = WebApplication.CreateBuilder(args);\n', metaDocuments: [meta], sizeBytes: 50, lineEnding: 'lf', encoding: 'utf-8' };
  else if (requestPath.endsWith('/review/runs/compare')) body = comparison;
  else if (requestPath.endsWith('/review/runs/trend')) body = trend;
  else if (requestPath.endsWith(`/${candidateRun.id}/report`)) body = candidateReport;
  else if (requestPath.endsWith('/review/runs')) body = { runs: [candidateRun, baselineRun] };
  else if (requestPath.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (requestPath.endsWith('/inputs')) body = { kinds: {} };
  else if (requestPath.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (requestPath.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (requestPath.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (requestPath.endsWith('/usage')) body = { generatedAt: candidateRun.finishedAt, runs: 2, inputTokens: 139000, outputTokens: 27000, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 1045000, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] };
  else if (requestPath === '/api/quotas') body = { at: candidateRun.finishedAt, ttlSeconds: 600, providers: [] };
  else if (requestPath.endsWith('/project')) body = { generatedAt: candidateRun.finishedAt, grades: [], findings: { open: 1, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path: '.' }, reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 1, folderCount: 1, bytes: 50, lines: 1, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
  else body = {};
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

const evidence = [];
for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, reducedMotion: 'reduce' });
  await page.addInitScript(() => localStorage.setItem('qs-layout', JSON.stringify({ explorerVisible: true, reviewVisible: true, explorerWidth: 260, reviewWidth: 640 })));
  await page.route('**/api/**', fulfillApi);
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', path);
  await page.goto(url.toString());
  await page.locator('.run-history-trigger').click();
  await page.locator('.run-open').first().click();
  const entry = page.locator('.comparison-entry');
  await entry.focus();
  await page.keyboard.press('Enter');
  const workbench = page.locator('.comparison-workbench');
  await workbench.waitFor();
  await page.locator('.comparison-provenance').waitFor();
  const audit = {
    baselineOptions: await page.locator('[aria-label="Comparison baseline"] option').count(),
    candidateOptions: await page.locator('[aria-label="Comparison candidate"] option').count(),
    metricCards: await page.locator('.comparison-metrics article').count(),
    findingDeltas: await page.locator('.comparison-finding').count(),
    keyboardOpened: await workbench.isVisible(),
    provenanceText: (await page.locator('.comparison-provenance').innerText()).replaceAll('\n', ' '),
  };
  if (audit.baselineOptions !== 1 || audit.candidateOptions !== 2 || audit.metricCards !== 6 || audit.findingDeltas !== 4)
    throw new Error(`${theme}: comparison acceptance audit failed: ${JSON.stringify(audit)}`);
  await workbench.scrollIntoViewIfNeeded();
  const workspace = `qs-83-run-comparison-${theme}.png`;
  const detail = `qs-83-run-comparison-${theme}-detail.png`;
  await page.screenshot({ path: join(output, workspace), fullPage: true });
  await workbench.screenshot({ path: join(output, detail) });
  evidence.push({ theme, workspace, detail, audit, dataSource: 'intercepted API fixture shaped from canonical review-run report v1' });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-83-run-comparison-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
