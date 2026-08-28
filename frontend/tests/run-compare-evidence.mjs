import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const path = 'src/QualityStudio.Api/Program.cs';
const hash = value => `sha256:${value.repeat(64).slice(0, 64)}`;
const runFields = {
  repositoryId: 'default', path, level: 'file', kind: 'code', cliType: 'codex', totalFiles: 1, completedFiles: 1,
  failedFiles: 0, skippedFiles: 0, errors: [], usageOperations: 1,
  usage: { inputTokens: 620, outputTokens: 140, cachedInputTokens: 80, reasoningOutputTokens: 30, durationMs: 4000 },
  estimate: null, tokenCap: null, costCap: null, costSpent: null, currency: null, priceStatus: 'unavailable',
  aggregateState: null, stopReason: null, deviation: null,
};
const baselineRun = { ...runFields, id: 'review-baseline', state: 'done', model: 'gpt-5.6-sol', thinkingLevel: 'high',
  createdAt: '2026-08-11T08:00:00Z', startedAt: '2026-08-11T08:00:01Z', finishedAt: '2026-08-11T08:00:05Z',
  files: [{ path, state: 'done', startedAt: '2026-08-11T08:00:01Z', finishedAt: '2026-08-11T08:00:05Z', error: null }] };
const candidateRun = { ...runFields, id: 'review-candidate', state: 'done', model: 'claude-opus-4-8', thinkingLevel: 'high',
  createdAt: '2026-08-18T08:00:00Z', startedAt: '2026-08-18T08:00:01Z', finishedAt: '2026-08-18T08:00:05Z',
  files: [{ path, state: 'done', startedAt: '2026-08-18T08:00:01Z', finishedAt: '2026-08-18T08:00:05Z', error: null }] };

const newFinding = { fingerprint: hash('1'), severity: 'high', title: 'Repository-scoped client can repoint its root', ruleId: 'security.idor', baselineState: null, candidateState: 'open', locations: [{ path, startLine: 163, startColumn: 9, endLine: 163, endColumn: 40 }] };
const resolvedFinding = { fingerprint: hash('2'), severity: 'medium', title: 'Missing replay guard on review start', ruleId: 'security.replay', baselineState: 'open', candidateState: null, locations: [{ path, startLine: 300, startColumn: 1, endLine: 300, endColumn: 20 }] };
const unchangedFinding = { fingerprint: hash('3'), severity: 'low', title: 'Static bearer credential has no live revocation', ruleId: 'security.credential', baselineState: 'accepted', candidateState: 'accepted', locations: [{ path, startLine: 139, startColumn: 1, endLine: 139, endColumn: 20 }] };

const compareResult = {
  status: 'available',
  baseline: { runId: baselineRun.id, status: 'found', error: null },
  candidate: { runId: candidateRun.id, status: 'found', error: null },
  comparison: {
    baselineRunId: baselineRun.id, candidateRunId: candidateRun.id,
    route: { compatible: false, differences: [`Model changed from '${baselineRun.model}' to '${candidateRun.model}'.`] },
    new: [newFinding], unchanged: [unchangedFinding], resolved: [resolvedFinding], dispositionChanged: [],
  },
};

const treeNode = { id: 'program', name: 'Program.cs', path, level: 'file', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 91, band: 'A', metaPath: '.quality/reviews/files/Program.cs.code.review-meta.json' } }, children: [] };
const meta = { reviewedAt: candidateRun.finishedAt, kind: 'code', reviewer: { agent: 'quality-reviewer', model: candidateRun.model }, grade: { score: 91, band: 'A', rationale: 'Route change under review.' }, summary: 'Comparable outcome snapshots are available.', findings: [] };
const emptyUsage = { generatedAt: candidateRun.finishedAt, runs: 2, inputTokens: 1240, outputTokens: 280, cachedInputTokens: 160, reasoningOutputTokens: 60, durationMs: 8000, byModel: [], byKind: [], byDay: [], byReviewRun: [] };

async function fulfillApi(route) {
  const url = new URL(route.request().url());
  const requestPath = url.pathname;
  let body;
  if (requestPath === '/api/repos') body = { repositories: [{ id: 'default', displayName: 'Quality Studio', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' };
  else if (requestPath === '/api/models') body = { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-08-18', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] };
  else if (requestPath.endsWith('/tree')) body = { nodes: [{ id: 'quality-studio', name: 'Quality Studio', path: '.', level: 'project', kinds: treeNode.kinds, children: [treeNode] }] };
  else if (requestPath.endsWith('/file')) body = { path, content: 'var builder = WebApplication.CreateBuilder(args);\n', metaDocuments: [meta], sizeBytes: 50, lineEnding: 'lf', encoding: 'utf-8' };
  else if (requestPath.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 };
  else if (requestPath.endsWith('/inputs')) body = { kinds: {} };
  else if (requestPath.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
  else if (requestPath.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
  else if (requestPath.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
  else if (requestPath.endsWith('/review/runs/pins')) body = { pinnedRunIds: [baselineRun.id] };
  else if (requestPath.endsWith('/review/runs/compare')) body = compareResult;
  else if (requestPath.endsWith('/review/runs/trend')) body = { points: [], nextCursor: null };
  else if (requestPath.endsWith('/review/runs')) body = { runs: [candidateRun, baselineRun] };
  else if (requestPath.endsWith('/usage')) body = emptyUsage;
  else if (requestPath === '/api/quotas') body = { at: candidateRun.finishedAt, ttlSeconds: 600, providers: [] };
  else if (requestPath.endsWith('/project')) body = { generatedAt: candidateRun.finishedAt, grades: [], findings: { open: 0, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path: '.' }, reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 1, folderCount: 1, bytes: 50, lines: 1, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
  else body = {};
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

const evidence = [];
for (const capture of [
  { name: 'dark', theme: 'dark', viewport: { width: 1440, height: 1000 } },
  { name: 'light', theme: 'light', viewport: { width: 1440, height: 1000 } },
]) {
  const page = await browser.newPage({ viewport: capture.viewport, reducedMotion: 'reduce' });
  await page.route('**/api/**', fulfillApi);
  const url = new URL(baseUrl);
  url.searchParams.set('theme', capture.theme);
  url.searchParams.set('path', path);
  await page.goto(url.toString());
  await page.locator('.run-history-trigger').waitFor();
  await page.locator('.run-history-trigger').click();
  await page.locator('.run-compare-actions').first().waitFor();
  const beforeFileName = `qs-83-run-history-before-${capture.name}.png`;
  await page.screenshot({ path: join(output, beforeFileName), fullPage: true });

  await page.locator('.run-compare-actions button', { hasText: 'Compare with…' }).first().click();
  await page.locator('[aria-label="Run comparison workbench"]').waitFor();
  await page.locator('.compare-route-warning').waitFor();
  const afterFileName = `qs-83-run-compare-after-${capture.name}.png`;
  await page.screenshot({ path: join(output, afterFileName), fullPage: true });

  evidence.push({
    ...capture,
    beforeFileName,
    afterFileName,
    routeWarningShown: await page.locator('.compare-route-warning').isVisible(),
    newCount: await page.locator('.run-detail-surface:has-text("New") .run-findings').first().locator('article').count(),
    pinnedBadgeShown: (await page.locator('.run-compare-actions button.pinned').first().textContent())?.includes('Pinned') ?? false,
  });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-83-run-compare-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
