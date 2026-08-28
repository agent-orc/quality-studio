// Evidence for QS-78: the shell remembers the last project and names an unreachable API.
// Drives the real stack (API + ng serve) in a real browser - no mocked services.
import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });

const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
const page = await context.newPage();
const steps = [];
const shot = async name => { await page.screenshot({ path: join(output, `qs-78-${name}.png`), fullPage: false }); return `qs-78-${name}.png`; };
const currentProject = () => page.locator('.repository-trigger strong').innerText();
const remembered = () => page.evaluate(() => window.localStorage.getItem('qs-last-repository'));
const expect = (condition, message) => { if (!condition) throw new Error(message); };

// 1. First visit with no memory: the shell picks the server default.
await page.goto(baseUrl);
await page.locator('[data-connection-state="live"]').waitFor({ timeout: 60000 });
const firstProject = await currentProject();
steps.push({ step: 'initial-load', project: firstProject, remembered: await remembered() });

// The switcher must show full paths (operator addendum: the path was truncated).
await page.locator('.repository-trigger').click();
await page.locator('.repository-menu').waitFor();
const entries = await page.locator('.repository-menu > button[role="menuitemradio"]').all();
expect(entries.length >= 2, `need >= 2 registered repositories for this evidence, found ${entries.length}`);
const paths = [];
for (const entry of entries) {
  const small = entry.locator('small');
  paths.push({
    path: (await small.innerText()).trim(),
    tooltip: await small.getAttribute('title'),
    // Truncation check: the rendered box must be tall enough to have wrapped, not clipped to one line.
    clipped: await small.evaluate(node => node.scrollWidth > node.clientWidth + 1),
  });
}
for (const entry of paths) expect(!entry.clipped, `repository path is still clipped: ${entry.path}`);
steps.push({ step: 'switcher-open-full-paths', paths, screenshot: await shot('switcher-full-path') });

// 2. Switch to the other project, then reload with no ?repo= - it must come back.
const target = entries[entries.length - 1];
const targetName = (await target.locator('strong').innerText()).trim();
expect(targetName !== firstProject, 'target project equals the initial project');
await target.click();
await page.locator(`.repository-trigger strong:text-is("${targetName}")`).waitFor({ timeout: 60000 });
await page.locator('[data-connection-state="live"]').waitFor({ timeout: 60000 });
const rememberedAfterSwitch = await remembered();
steps.push({ step: 'manual-switch', project: targetName, remembered: rememberedAfterSwitch });

await page.goto(baseUrl); // deliberately no ?repo= param
await page.locator('[data-connection-state="live"]').waitFor({ timeout: 60000 });
const restored = await currentProject();
expect(restored === targetName, `last project was not restored: expected ${targetName}, got ${restored}`);
steps.push({ step: 'restored-after-reload', project: restored, remembered: await remembered(), screenshot: await shot('last-project-restored') });

// 3. API down: the notice bar must be unmistakable and offer a retry.
await page.route('**/api/**', route => route.abort('connectionrefused'));
await page.goto(baseUrl);
const bar = page.locator('.api-status-bar');
await bar.waitFor({ timeout: 60000 });
const barText = (await bar.locator('.api-status-text').innerText()).trim();
const retry = bar.locator('.api-status-retry');
expect(await retry.isVisible(), 'retry action is not visible in the API-down notice bar');
const spinners = await page.locator('.project-view-loading, .review-controls-loading').count();
const offlineState = await page.locator('.health').getAttribute('data-connection-state');
expect(offlineState === 'offline', `expected connection state offline, got ${offlineState}`);
const rememberedDuringOutage = await remembered();
steps.push({
  step: 'api-down',
  connectionState: offlineState,
  noticeText: barText,
  retryVisible: true,
  strandedSpinners: spinners,
  remembered: rememberedDuringOutage,
  screenshot: await shot('api-down-notice'),
});

// 4. Recovery: with the API reachable again, Retry clears the notice.
await page.unroute('**/api/**');
await retry.click();
await page.locator('[data-connection-state="live"]').waitFor({ timeout: 60000 });
expect(await bar.count() === 0, 'notice bar survived a recovered connection');
const projectAfterRetry = await currentProject();
// An outage parks the shell on the legacy 'default' entry; recovering must not silently
// re-point the operator at another project or forget what they had open.
expect(projectAfterRetry === targetName, `retry lost the remembered project: expected ${targetName}, got ${projectAfterRetry}`);
steps.push({ step: 'retry-recovered', project: projectAfterRetry, remembered: await remembered(), screenshot: await shot('api-recovered') });

await browser.close();
const report = { capturedAt: new Date().toISOString(), baseUrl, pass: true, steps };
await writeFile(join(output, 'qs-78-shell-continuity-evidence.json'), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report, null, 2));
