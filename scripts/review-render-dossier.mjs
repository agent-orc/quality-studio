import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { createRequire } from 'node:module';
import { setTimeout as delay } from 'node:timers/promises';

const repositoryRoot = resolve(import.meta.dirname, '..');
const frontendRoot = resolve(repositoryRoot, 'frontend');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const require = createRequire(import.meta.url);
const { chromium } = require(resolve(frontendRoot, 'node_modules/playwright-core'));
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const port = await freePort();
const ngCli = resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js');
const server = spawn(process.execPath, [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(port)], {
  cwd: frontendRoot,
  env: process.env,
  stdio: ['ignore', 'pipe', 'pipe'],
});

try {
  await waitForHttp(`http://127.0.0.1:${port}`, 60_000);
  const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    if (process.env.DEBUG_RENDER_PROBE) page.on('console', message => console.error('browser:', message.text()));
    const runs = [];
    const timing = new Map();
    await page.route('**/api/**', async route => {
      const request = route.request();
      const url = new URL(request.url());
      const path = url.pathname;
      if (path.endsWith('/api/models')) return json(route, modelCatalog());
      if (request.method() === 'POST' && path.endsWith('/review/estimate')) return json(route, preflight());
      if (request.method() === 'POST' && path.endsWith('/review')) {
        const id = `review-render-${runs.length + 1}`;
        const run = reviewRun(id, 'queued');
        runs.unshift(run);
        timing.set(id, { terminalAvailableAt: performance.now() + 100, terminalResponseAt: null });
        if (process.env.DEBUG_RENDER_PROBE) console.error('queued', id);
        return json(route, run);
      }
      if (request.method() === 'GET' && path.endsWith('/review/runs')) {
        const now = performance.now();
        const projected = runs.map(run => {
          const sample = timing.get(run.id);
          const done = sample && now >= sample.terminalAvailableAt;
          if (done) sample.terminalResponseAt = now;
          return done ? reviewRun(run.id, 'done') : run;
        });
        if (process.env.DEBUG_RENDER_PROBE) console.error('poll', projected.map(run => `${run.id}:${run.state}`).join(','));
        return json(route, { runs: projected });
      }
      if (path.endsWith('/repos')) return json(route, { repositories: [repository()], defaultRepositoryId: 'default' });
      if (path.endsWith('/tree')) return json(route, { path: '.', nodes: tree() });
      if (path.endsWith('/project')) return json(route, project());
      if (path.endsWith('/file')) return json(route, file());
      if (path.endsWith('/scan')) return json(route, { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 });
      if (path.endsWith('/inputs')) {
        const empty = kind => ({ kind, level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] });
        return json(route, { kinds: { code: empty('code'), security: empty('security'), performance: empty('performance') } });
      }
      if (path.endsWith('/guidelines')) return json(route, { guidelines: [], catalogue: [], traces: [] });
      if (path.endsWith('/risk')) return json(route, { days: 90, currentCommit: null, rows: [], matrix: [] });
      if (path.endsWith('/usage')) return json(route, usage());
      if (path.endsWith('/quotas')) return json(route, { at: new Date().toISOString(), ttlSeconds: 60, providers: [] });
      if (path.endsWith('/handover')) return json(route, { targetConfigured: false, dryRun: true });
      return json(route, {});
    });
    page.on('dialog', dialog => dialog.accept());
    await page.goto(`http://127.0.0.1:${port}/?repo=default&path=src%2FCartTotals.cs&kind=code`);
    const reviewIntent = page.locator('.scope-review-launcher .review-intent');
    await reviewIntent.waitFor({ state: 'visible' });
    const samples = [];
    for (let iteration = 1; iteration <= 5; iteration++) {
      if (iteration > 1) {
        await page.locator('.scope-review-launcher .active-run-actions')
          .getByRole('button', { name: 'Review again' }).click();
      }
      await reviewIntent.click();
      await page.locator('.scope-review-launcher .preflight-sheet')
        .getByRole('button', { name: 'Start review', exact: true }).click();
      const id = `review-render-${iteration}`;
      await page.locator('.scope-review-launcher .active-run-strip[data-state="done"]').waitFor();
      const renderedAt = performance.now();
      const sample = timing.get(id);
      samples.push({
        iteration,
        terminalToVisibleMs: round(renderedAt - sample.terminalAvailableAt),
        responseToVisibleMs: round(renderedAt - sample.terminalResponseAt),
      });
    }
    const result = {
      measuredAt: new Date().toISOString(),
      browser: await browser.version(),
      mechanism: 'real Angular app and current preflight/start workflow; mocked local API marks each run done 100 ms after queueing; existing 1,500 ms polling path unchanged',
      samples,
      terminalToVisible: summarize(samples.map(sample => sample.terminalToVisibleMs)),
      responseToVisible: summarize(samples.map(sample => sample.responseToVisibleMs)),
    };
    await mkdir(resultsRoot, { recursive: true });
    await writeFile(resolve(resultsRoot, 'review-render-latency.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
  } finally {
    await browser.close();
  }
} finally {
  server.kill('SIGTERM');
}

function repository() {
  return { id: 'default', displayName: 'Render fixture', rootPath: '/fixture', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null };
}

function tree() {
  const kinds = { code: kind(), security: kind(), performance: kind() };
  return [{ id: 'root', name: 'Render fixture', level: 'repository', path: '.', kinds, findingsCount: 0, findingCounts: counts(), reviewedAt: null, sizeBytes: 240, lineCount: 12, coverage: coverage(), excluded: [], children: [
    { id: 'file', name: 'CartTotals.cs', level: 'file', path: 'src/CartTotals.cs', kinds, findingsCount: 0, findingCounts: counts(), reviewedAt: null, sizeBytes: 240, lineCount: 12, coverage: coverage(), excluded: [], children: [] },
  ] }];
}

function kind() { return { direct: 'missing', descendants: 'missing', overall: 'missing', score: null, band: null, metaPath: null }; }
function counts() { return { open: 0, accepted: 0, waived: 0, falsePositive: 0, resolved: 0 }; }
function coverage() { return { state: 'unknown', coveredLines: 0, totalLines: 0, coveredBranches: 0, totalBranches: 0, linePercent: null, branchPercent: null, commit: null, measuredAt: null, filesWithData: 0 }; }
function file() { return { path: 'src/CartTotals.cs', content: 'public static class CartTotals { }\n', metaDocuments: [], sizeBytes: 35, lineEnding: 'lf', encoding: 'utf-8', coverage: coverage() }; }
function project() { return { generatedAt: new Date().toISOString(), grades: [], findings: { open: 0, bySeverity: {}, byReviewState: {}, path: '.' }, staleness: { fresh: 0, stale: 0, missing: 1, total: 1, path: '.' }, reviewCoverage: { reviewedFiles: 0, totalFiles: 1, percent: 0, path: '.' }, testCoverage: { status: 'unavailable', linePercent: null, coveredLines: null, totalLines: null, source: null, path: '.' }, metrics: { fileCount: 1, folderCount: 1, bytes: 35, lines: 1, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] }; }
function usage() { return { generatedAt: new Date().toISOString(), runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 0, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] }; }
function modelCatalog() { return { schemaVersion: 1, policyVersion: 'render-fixture', evidenceAsOfDate: '2026-08-11', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['low', 'medium', 'high'], models: [] }; }
function preflight() { return { repositoryId: 'default', path: 'src/CartTotals.cs', level: 'file', kind: 'code', model: null, thinkingLevel: null, cliType: 'codex', estimate: { files: 1, operations: 1, promptCharacters: 4000, inputTokens: 1000, outputTokens: 200, cost: null, currency: null, priceStatus: 'unknownModel', historySamples: 0, method: 'render fixture', expectedFreshSkips: 0 }, tokenCap: 100000, costCap: null, recommendation: { policyVersion: 'render-fixture', recommendedModel: 'runner-default', recommendedThinkingLevel: 'model-default', capabilityTier: 'frontier', score: 100, correctnessFloor: 'runner-default', reason: 'Controlled rendering fixture.', selectionSource: 'fixture' }, overrideBelowFloor: false }; }
function reviewRun(id, state) { const done = state === 'done'; return { id, repositoryId: 'default', path: 'src/CartTotals.cs', level: 'file', kind: 'code', model: null, thinkingLevel: null, cliType: 'codex', state, totalFiles: 1, completedFiles: done ? 1 : 0, failedFiles: 0, createdAt: new Date().toISOString(), startedAt: new Date().toISOString(), finishedAt: done ? new Date().toISOString() : null, files: [], errors: [], usageOperations: done ? 1 : 0, usage: { inputTokens: done ? 1000 : null, outputTokens: done ? 200 : null, cachedInputTokens: done ? 500 : null, reasoningOutputTokens: done ? 50 : null, durationMs: done ? 100 : 0 }, estimate: preflight().estimate, tokenCap: 100000, costCap: null, costSpent: null, currency: null, priceStatus: 'unknownModel', skippedFiles: 0, aggregateState: null, stopReason: null, deviation: null }; }

function json(route, body) { return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }); }

async function freePort() {
  const server = createServer();
  await new Promise((resolvePromise, rejectPromise) => server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
  const port = server.address().port;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
}

async function waitForHttp(url, timeoutMs) {
  const started = performance.now();
  while (performance.now() - started < timeoutMs) {
    try { if ((await fetch(url)).ok) return; } catch { /* build not ready */ }
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${url}`);
}

function summarize(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return { min: round(sorted[0]), median: round(sorted[Math.floor(sorted.length / 2)]), max: round(sorted.at(-1)) };
}

function round(value) { return Number(value.toFixed(2)); }
