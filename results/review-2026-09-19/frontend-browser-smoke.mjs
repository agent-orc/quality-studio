import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '../../frontend/node_modules/playwright-core/index.mjs';
const output = dirname(fileURLToPath(import.meta.url));
const baseUrl = process.env.QS_URL || 'http://127.0.0.1:4229/';
const browser = await chromium.launch({ executablePath: process.env.CHROME_BIN || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const path = 'src/Reviewed.cs', fingerprint = 'sha256:history-a';
const finding = { id: 'history-a', fingerprint, ruleId: 'history-regression', aspect: 'security', severity: 'medium',
  title: 'Preserve this security finding', description: 'Deterministic browser fixture.', recommendation: 'Keep navigation state.',
  locations: [{ path, range: { start: { line: 1, column: 1 }, end: { line: 1, column: 12 } } }] };
const kinds = Object.fromEntries(['code', 'security', 'performance'].map(kind => [kind,
  { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 90, band: 'A', metaPath: 'fixture.json' }]));
const fileNode = { id: 'file', name: 'Reviewed.cs', path, level: 'file', kinds, children: [], hasChildren: false };
const rootNode = { id: 'root', name: 'Fixture project', path: '.', level: 'project', kinds, children: [], hasChildren: true, childCount: 1 };
const project = { generatedAt: '', grades: [],
  findings: { open: 1, bySeverity: { critical: 0, high: 0, medium: 1, low: 0, info: 0 }, byReviewState: { fresh: 1, stale: 0 }, path },
  staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path }, reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path },
  testCoverage: { status: 'unavailable', linePercent: null, coveredLines: 0, totalLines: 0, source: '', path },
  metrics: { fileCount: 1, folderCount: 1, bytes: 50, lines: 2, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] }, hotspots: [] };
const usage = { generatedAt: '', runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0,
  durationMs: 0, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] };
const repositories = ['a', 'b'].map(id => ({ id, displayName: 'Fixture ' + id.toUpperCase(), rootPath: '/fixture/' + id,
  enabledReviewKinds: ['code', 'security', 'performance'], isArchived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }));
const errors = [], requests = [];
let delayedTreeRepo = null;
let failRepositoryExport = true;
const hostileText = '<img src=x onerror=window.__findingXss=true> [unsafe](javascript:alert(1))';
const run = { id: 'run-auth', repositoryId: 'a', path, level: 'file', kind: 'security', state: 'done',
  model: hostileText, thinkingLevel: 'high', cliType: 'codex', modelSource: 'explicit',
  files: [{ path, state: 'done', error: null }], completedFiles: 1, totalFiles: 1, failedFiles: 0, skippedFiles: 0, errors: [], usageOperations: 0,
  usage, tokenCap: null, costSpent: null, costCap: null, currency: null, priceStatus: 'unknownModel',
  createdAt: '2026-09-19T20:00:00Z' };
const report = { run: { ...run, revision: 1, completeness: 'complete' }, subject: { manifestHash: 'fixture' },
  execution: { reviewed: 1, reusedFresh: 0 }, summary: { score: 90, grade: 'A', findings: { total: 1 } },
  observations: [{ unitId: 'file', path, level: 'file', outcome: 'done', producedByRun: true,
    grade: { score: 90, band: 'A' }, findings: [{ ...finding, title: hostileText, description: hostileText }] }] };

async function fixturePage(blockStorage = false, hosted = false) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && message.text().startsWith('ERROR')) errors.push(message.text()); });
  if (hosted) await page.addInitScript(() => {
    localStorage.setItem('qs-api-token', 'smoke-token');
    window.__downloadUrls = { created: [], revoked: [] };
    const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
    URL.createObjectURL = blob => { const url = create(blob); window.__downloadUrls.created.push({ url, type: blob.type }); return url; };
    URL.revokeObjectURL = url => { window.__downloadUrls.revoked.push(url); revoke(url); };
  });
  if (blockStorage) await page.addInitScript(() => Object.defineProperty(window, 'localStorage', {
    configurable: true, get() { throw new DOMException('Storage blocked by fixture', 'SecurityError'); } }));
  await page.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url());
    requests.push({ method: request.method(), path: url.pathname });
    assert.equal(request.method(), 'GET', 'No repository mutations in this smoke test');
    if (hosted) assert.equal(request.headers()['authorization'], 'Bearer smoke-token');
    const repo = url.pathname.match(/^\/api\/repos\/([^/]+)/)?.[1];
    let body;
    if (url.pathname === '/api/repos') body = { repositories, defaultRepositoryId: 'a' };
    else if (url.pathname === '/api/models') body = { schemaVersion: 1, models: [], thinkingLevels: [] };
    else if (url.pathname === '/api/models/default') body = null;
    else if (url.pathname === '/api/quotas') body = { at: '', ttlSeconds: 60, providers: [] };
    else if (url.pathname.endsWith('/tree/v2/search')) body = { schemaVersion: 2, nodes: [fileNode], nextCursor: null };
    else if (url.pathname.endsWith('/tree/v2')) {
      if (repo === delayedTreeRepo) await new Promise(resolve => setTimeout(resolve, 350));
      body = { schemaVersion: 2, nodes: url.searchParams.has('parentId') ? [fileNode] : [rootNode], nextCursor: null, snapshotEtag: '"fixture"' };
    } else if (url.pathname.endsWith('/file')) body = { path, content: 'public class Repository' + repo.toUpperCase() + ' {}\n', sizeBytes: 30, lineEnding: 'lf', encoding: 'utf-8',
      metaDocuments: [{ reviewedAt: '2026-09-19T20:00:00Z', kind: 'security', reviewer: { agent: 'fixture', model: 'deterministic' },
        grade: { score: 90, band: 'A', rationale: 'Fixture.' }, summary: 'Navigation fixture.', findings: [finding] }] };
    else if (url.pathname.endsWith('/project')) body = project;
    else if (url.pathname.endsWith('/scan')) body = { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0, invalidCount: 0 };
    else if (url.pathname.endsWith('/inputs')) body = { kinds: {} };
    else if (url.pathname.endsWith('/guidelines')) body = { guidelines: [], catalogue: [], traces: [] };
    else if (url.pathname.endsWith('/risk')) body = { days: 90, currentCommit: null, rows: [], matrix: [] };
    else if (url.pathname.endsWith('/findings/suppressions')) body = { schemaVersion: 1, revision: 0, rules: [] };
    else if (url.pathname.endsWith('/handover')) body = { targetConfigured: false, dryRun: true };
    else if (url.pathname.endsWith('/review/runs/pins')) body = { pinnedRunIds: [] };
    else if (url.pathname.endsWith('/review/runs/trend')) body = { points: [], nextCursor: null };
    else if (url.pathname.endsWith('/run-auth/report') && url.searchParams.get('format') === 'json') body = report;
    else if (url.pathname.endsWith('/report')) {
      if (!url.pathname.includes('/review/runs/') && failRepositoryExport) {
        return route.fulfill({ status: 403, contentType: 'text/plain', body: 'Denied fixture export' });
      }
      return route.fulfill({ status: 200, contentType: 'text/html',
        headers: { 'Content-Disposition': 'attachment; filename=fixture.html' },
        body: '<script>window.__exportXss=true</script><p>download fixture</p>' });
    }
    else if (url.pathname.endsWith('/review/runs')) body = { runs: hosted ? [run] : [] };
    else if (url.pathname.endsWith('/usage')) body = usage;
    else throw new Error('Unmocked API request: ' + url.pathname);
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  return page;
}
async function switchTo(page, name) {
  await page.locator('.repository-trigger').click();
  await page.getByRole('menuitemradio', { name: new RegExp(name) }).click();
  await page.locator('[data-connection-state="live"]').waitFor();
}
try {
  const page = await fixturePage();
  const initial = new URL(baseUrl);
  initial.search = new URLSearchParams({ repo: 'a', path, kind: 'security', finding: fingerprint }).toString();
  await page.goto(initial.href);
  await page.waitForFunction(() => document.querySelector('.code-viewport')?.textContent?.includes('RepositoryA'));
  await switchTo(page, 'Fixture B');
  await page.waitForFunction(() => {
    const p = new URL(location.href).searchParams;
    return p.get('repo') === 'b' && p.get('path') === '.';
  });
  const historyLength = await page.evaluate(() => history.length);
  delayedTreeRepo = 'a';
  await page.goBack();
  await page.waitForFunction(() => document.querySelector('.code-viewport')?.textContent?.includes('RepositoryA'));
  await page.waitForFunction(() => {
    const p = new URL(location.href).searchParams;
    return p.get('repo') === 'a' && p.get('path') === 'src/Reviewed.cs' && p.get('finding') === 'sha256:history-a';
  });
  assert.equal(await page.evaluate(() => history.length), historyLength);
  assert.match(await page.locator('.review-pane').innerText(), /Preserve this security finding/);
  await page.screenshot({ path: join(output, 'frontend-history-restored--mocked.png'), fullPage: true });
  await page.goForward();
  await page.waitForFunction(() => new URL(location.href).searchParams.get('repo') === 'b');
  assert.equal(await page.evaluate(() => history.length), historyLength);
  await page.close();
  const blocked = await fixturePage(true);
  await blocked.goto(baseUrl + '?path=src%2FReviewed.cs&kind=security');
  await blocked.waitForFunction(() => document.querySelector('.code-viewport')?.textContent?.includes('RepositoryA'));
  await blocked.getByRole('button', { name: 'Switch to light theme' }).click();
  await blocked.waitForFunction(() => document.documentElement.dataset.theme === 'light');
  await switchTo(blocked, 'Fixture B');
  await blocked.waitForFunction(() => new URL(location.href).searchParams.get('repo') === 'b');
  await blocked.locator('[data-transition-state]').waitFor({ state: 'detached' });
  await blocked.screenshot({ path: join(output, 'frontend-storage-blocked-usable--mocked.png'), fullPage: true });
  await blocked.close();
  const hosted = await fixturePage(false, true);
  finding.title = hostileText;
  finding.description = hostileText;
  await hosted.goto(baseUrl + '?repo=a&path=src%2FReviewed.cs&kind=security');
  await hosted.locator('.finding-card').first().waitFor();
  assert.match(await hosted.locator('.finding-card').first().innerText(), /<img src=x/);
  assert.equal(await hosted.locator('.finding-card img').count(), 0);
  await hosted.locator('qs-run-history .run-history-trigger').click();
  await hosted.locator('.run-open').first().click();
  await hosted.locator('.run-exports button').first().waitFor();
  assert.match(await hosted.locator('.run-provenance').innerText(), /<img src=x/);
  assert.equal(await hosted.locator('.run-detail-surface img').count(), 0);
  const documentUrl = hosted.url();
  const downloading = hosted.waitForEvent('download');
  await hosted.locator('.run-exports button').filter({ hasText: /^HTML$/ }).click();
  const download = await downloading;
  assert.equal(download.suggestedFilename(), 'quality-run-run-auth.html');
  const stream = await download.createReadStream(), chunks = [];
  for await (const chunk of stream) chunks.push(chunk);
  assert.match(Buffer.concat(chunks).toString(), /download fixture/);
  assert.equal(hosted.url(), documentUrl);
  await hosted.getByRole('button', { name: 'Download repository report' }).click();
  await hosted.getByRole('alert').filter({ hasText: 'not permitted' }).waitFor();
  await hosted.screenshot({ path: join(output, 'frontend-export-error--mocked.png'), fullPage: true });
  failRepositoryExport = false;
  const repositoryDownloading = hosted.waitForEvent('download');
  await hosted.getByRole('button', { name: 'Download repository report' }).click();
  assert.equal((await repositoryDownloading).suggestedFilename(), 'quality-repository-report.html');
  await hosted.waitForFunction(() => window.__downloadUrls.revoked.length === 2);
  const urls = await hosted.evaluate(() => window.__downloadUrls);
  assert.ok(urls.created.every(entry => entry.type === 'application/octet-stream' && urls.revoked.includes(entry.url)));
  assert.equal(await hosted.evaluate(() => window.__findingXss || window.__exportXss), undefined);
  assert.equal(hosted.url(), documentUrl);
  assert.ok(requests.every(request => !request.path.includes('smoke-token')));
  await hosted.close();
  assert.deepEqual(errors, []);
  await writeFile(join(output, 'frontend-browser-smoke.json'), JSON.stringify({
    capturedAt: new Date().toISOString(), baseUrl, assertions: ['Back restores repository, file, security finding and existing history entry',
      'Forward survives delayed tree loading', 'Blocked storage allows startup, theme change and repository switch',
      'Hosted exports carry bearer auth, download octet-stream blobs and release their URLs', 'Export 403 is visible and retry succeeds', 'Hostile finding/model strings remain text and downloaded HTML never executes', 'Only mocked GET API requests and no page exceptions'], apiRequestCount: requests.length, pageErrors: errors }, null, 2) + '\n');
  console.log('PASS: history, delayed restore, blocked storage; ' + requests.length + ' mocked GETs; 0 page errors.');
} finally { await browser.close(); }
