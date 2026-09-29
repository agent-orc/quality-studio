import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

// QS-115 evidence: the risk view's complexity column and the size-weighted directory projection,
// against a live dev stack (`node scripts/dev-stack.mjs`). Tree, risk, coverage and complexity come
// from the real API; this checkout carries no file reviews, so file grades are injected into the
// tree responses and the directory projection is the one those injected grades imply.
const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const base = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const repo = process.env.QS_REPO ?? 'default';
const folder = process.env.QS_PATH ?? 'backend/AgentOrchestrator.CodeQuality';
await mkdir(output, { recursive: true });

const bandOf = score => score >= 90 ? 'A' : score >= 80 ? 'B' : score >= 70 ? 'C' : score >= 60 ? 'D' : 'F';
const scores = [92, 74, 58, 85, 66];

/** Grades the first files of the folder's level in place and returns the projection they imply. */
function gradeFiles(nodes, injected) {
  const files = nodes.filter(node => node.level === 'file');
  let weighted = 0;
  let lines = 0;
  files.slice(0, scores.length).forEach((node, index) => {
    const score = scores[index];
    node.kinds.code = { ...node.kinds.code, direct: 'fresh', overall: 'fresh', score, band: bandOf(score) };
    const weight = Math.max(1, node.lineCount ?? Math.round((node.sizeBytes ?? 0) / 40));
    weighted += score * weight;
    lines += weight;
    injected?.push({ path: node.path, score, weight });
  });
  if (!lines) return null;
  const score = Math.round(weighted / lines);
  return { score, band: bandOf(score), gradedFiles: Math.min(files.length, scores.length), files: files.length, weightedLines: lines, basis: 'size-weighted-file-grades' };
}

const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
const results = [];
for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1400 }, deviceScaleFactor: 1 });
  const injected = [];
  let folderId = null;
  let projection = null;
  await page.route(/\/api\/repos\/[^/]+\/tree\/v2(?:\/search)?(?:\?|$)/, async route => {
    const response = await route.fetch();
    const body = await response.json();
    const request = new URL(route.request().url());
    if (folderId && request.searchParams.get('parentId') === folderId) {
      injected.length = 0;
      gradeFiles(body.nodes ?? [], injected);
    }
    for (const node of body.nodes ?? body.results ?? []) {
      if (node?.path !== folder || !node.kinds?.code) continue;
      if (!projection) {
        folderId = node.id;
        const level = await page.request.get(`${request.origin}${request.pathname.replace(/\/search$/, '')}?limit=500&view=files&parentId=${encodeURIComponent(node.id)}`);
        projection = gradeFiles((await level.json()).nodes ?? [], null);
      }
      node.kinds.code = { ...node.kinds.code, score: null, band: null, projection };
    }
    await route.fulfill({ response, json: body });
  });

  await page.goto(`${base}?repo=${repo}&theme=${theme}&kind=code&path=${encodeURIComponent(folder)}`);
  await page.locator('.risk-view').waitFor();
  await page.locator('.risk-view summary').click();
  await page.locator('.risk-view [role="cell"].complex, .risk-view [role="row"] [role="cell"]').first().waitFor();
  await page.locator('.grade-cell').first().waitFor();
  await page.locator('.rollups .projected').first().waitFor();

  const header = await page.locator('.risk-view summary').innerText();
  if (!header.includes('complexity')) throw new Error(`${theme}: risk view header does not mention complexity: ${header}`);
  const complexityCells = await page.locator('.risk-view [role="row"] [role="cell"]:nth-child(4)').allInnerTexts();
  const projected = await page.locator('.projected').count();
  const rollupText = await page.locator('.rollups').innerText();

  await page.screenshot({ path: join(output, `qs-115-metrics-${theme}.png`), fullPage: true });
  await page.locator('.risk-view').screenshot({ path: join(output, `qs-115-risk-complexity-${theme}.png`) });
  results.push({ theme, header, complexitySample: complexityCells.slice(0, 8), projectedCells: projected, rollups: rollupText, projection, injected });
  await page.close();
}
await browser.close();
await writeFile(join(output, 'qs-115-metrics-evidence.json'), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
