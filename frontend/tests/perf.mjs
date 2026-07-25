import { chromium } from 'playwright-core';

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
await page.goto(process.env.QS_URL ?? 'http://127.0.0.1:4200/?theme=dark');
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

const measures = await page.evaluate(() => performance.getEntriesByType('measure').map(entry => ({
  name: entry.name,
  durationMs: Number(entry.duration.toFixed(2)),
})));
const result = { measuredAt: new Date().toISOString(), browser: await browser.version(), payloadBytes: Buffer.byteLength(payload), fileRequestCount, largeFileMode, highlightedTokenCount, measures, events };
console.log(JSON.stringify(result, null, 2));
await browser.close();

if (measures.some(item => item.name === 'qs.tree.toggle' && item.durationMs >= 50) ||
    measures.some(item => item.name === 'qs.file.first-content' && item.durationMs >= 150) ||
    measures.some(item => item.name === 'qs.review.aspect-switch' && item.durationMs >= 50) ||
    fileRequestCount !== 2 || !largeFileMode || highlightedTokenCount !== 0) process.exitCode = 1;
