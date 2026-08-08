import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const executablePath = process.env.CHROME_BIN || (process.platform === 'win32'
  ? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
  : chromium.executablePath());
const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
const events = [];
let fileRequestCount = 0;
let resolveInitialFile;
const initialFileRequested = new Promise(resolve => resolveInitialFile = resolve);
page.on('console', message => {
  try {
    const event = JSON.parse(message.text());
    if (event.event?.startsWith('qs.')) events.push(event);
  } catch { /* Browser diagnostics that are not structured app events. */ }
});

// Exercise the worst supported payload while keeping transport out of the scripting measurement.
const payload = Array.from({ length: 6000 }, (_, i) => `${i + 1}: public static string ReviewLine${i} => "quality";`).join('\n');
const meta = kind => ({ reviewedAt: '2026-07-11T16:20:00.000Z', kind, reviewer: { agent: 'perf-harness', model: 'deterministic' }, grade: { score: kind === 'code' ? 91 : 72, band: kind === 'code' ? 'A' : 'C', rationale: 'Harness metadata.' }, summary: 'Aspect switching stays local.', findings: [] });
await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => {
  fileRequestCount++;
  resolveInitialFile();
  return route.fulfill({
  contentType: 'application/json',
  body: JSON.stringify({ path: 'src/QualityStudio.Api/ApiContracts.cs', content: payload, metaDocuments: [meta('code'), meta('performance')] }),
  });
});
const project = {
  generatedAt: '2026-07-25T10:00:00Z',
  grades: ['code', 'security', 'performance'].map(kind => ({ kind, state: 'fresh', score: 90, band: 'A', path: 'src/QualityStudio.Api/Program.cs' })),
  findings: { open: 3, bySeverity: { critical: 0, high: 1, medium: 1, low: 1, info: 0 }, byReviewState: { fresh: 3, stale: 0 }, path: 'src/QualityStudio.Api/Program.cs' },
  staleness: { fresh: 4000, stale: 500, missing: 500, total: 5000, path: 'src/QualityStudio.Api/Program.cs' },
  reviewCoverage: { reviewedFiles: 4500, totalFiles: 5000, percent: 90, path: 'src/QualityStudio.Api/Program.cs' },
  testCoverage: { status: 'reported', linePercent: 82, coveredLines: 8200, totalLines: 10000, source: 'coverage.xml', path: 'src/QualityStudio.Api/Program.cs' },
  metrics: {
    fileCount: 5000, folderCount: 420, bytes: 25000000, lines: 300000,
    languages: [{ language: 'C#', files: 5000, lines: 300000, bytes: 25000000, path: 'src/QualityStudio.Api/Program.cs' }],
    fileSizeDistribution: [{ label: '< 1 KB', count: 1000 }, { label: '1–10 KB', count: 3000 }, { label: '10–100 KB', count: 1000 }],
    folderSizeDistribution: [{ label: '< 1 KB', count: 20 }, { label: '1–10 KB', count: 300 }, { label: '10–100 KB', count: 100 }],
    duplicationCandidates: [], dependencyEdges: [],
  },
  hotspots: Array.from({ length: 30 }, (_, index) => ({ path: `src/file-${index}.cs`, churn: 100 - index, grade: 70 + index % 20, findings: index % 4, findingsPerKloc: index / 10, risk: 30 - index })),
};
await page.route(/\/api\/(?:repos\/[^/]+\/)?project(?:\?|$)/, route => route.fulfill({
  contentType: 'application/json',
  body: JSON.stringify(project),
}));
await page.goto(process.env.QS_URL ?? 'http://127.0.0.1:4200/?theme=dark&path=src%2FQualityStudio.Api%2FProgram.cs');
await initialFileRequested;
await page.locator('.tree-row').first().click();
await page.locator('.tree-row').first().click();
await page.getByRole('textbox', { name: 'Filter files' }).fill('Program.cs');
// Container clicks now select their list view, so open the filtered file row
// directly instead of relying on the previous file selection to be retained.
await page.locator('.tree-row').first().click();
await page.waitForFunction(() => performance.getEntriesByName('qs.file.first-content').length >= 1);
const largeFileMode = await page.getByText('Large file · plain text', { exact: true }).isVisible();
const highlightedTokenCount = await page.locator('.code-line code span:not(.tok-plain)').count();
await page.getByRole('tab', { name: /performance/i }).click();
await page.waitForFunction(() => performance.getEntriesByName('qs.review.aspect-switch').length >= 1);
await page.getByRole('textbox', { name: 'Filter files' }).fill('');
const dashboardStarted = await page.evaluate(() => performance.now());
await page.locator('[data-node-id="quality-studio"]').click();
await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });
const dashboardDurationMs = await page.evaluate(start => Number((performance.now() - start).toFixed(2)), dashboardStarted);

const measures = await page.evaluate(() => performance.getEntriesByType('measure').map(entry => ({
  name: entry.name,
  durationMs: Number(entry.duration.toFixed(2)),
})));
const result = { measuredAt: new Date().toISOString(), browser: await browser.version(), payloadBytes: Buffer.byteLength(payload), repositoryFileCount: project.metrics.fileCount, dashboardDurationMs, fileRequestCount, largeFileMode, highlightedTokenCount, measures, events };
console.log(JSON.stringify(result, null, 2));
if (process.env.JOB_RESULTS_DIR) {
  const output = resolve(process.env.JOB_RESULTS_DIR);
  await mkdir(output, { recursive: true });
  await writeFile(join(output, 'qs-56-perf.json'), `${JSON.stringify(result, null, 2)}\n`);
}
await browser.close();

if (measures.some(item => item.name === 'qs.tree.toggle' && item.durationMs >= 50) ||
    measures.some(item => item.name === 'qs.file.first-content' && item.durationMs >= 150) ||
    measures.some(item => item.name === 'qs.review.aspect-switch' && item.durationMs >= 50) ||
    dashboardDurationMs >= 150 || fileRequestCount !== 2 || !largeFileMode || highlightedTokenCount !== 0) process.exitCode = 1;
