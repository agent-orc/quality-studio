import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';

const htmlPath = resolve(process.argv[2]);
const output = resolve(process.argv[3] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });

const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = [];
for (const capture of [
  { name: 'light', colorScheme: 'light', viewport: { width: 1280, height: 900 } },
  { name: 'dark', colorScheme: 'dark', viewport: { width: 1280, height: 900 } },
  { name: 'narrow', colorScheme: 'light', viewport: { width: 480, height: 900 } },
]) {
  const page = await browser.newPage({ viewport: capture.viewport, reducedMotion: 'reduce' });
  await page.emulateMedia({ colorScheme: capture.colorScheme });
  const networkRequests = [];
  page.on('request', request => {
    const protocol = new URL(request.url()).protocol;
    if (!['file:', 'data:', 'about:'].includes(protocol)) networkRequests.push(request.url());
  });
  await page.goto(pathToFileURL(htmlPath).href);
  await page.locator('main[data-run-id][data-run-revision]').waitFor();

  const sections = await page.locator('h2').allTextContents();
  for (const required of ['Verdicts', 'Findings', 'Token ledger', 'Provenance']) {
    assert.ok(sections.includes(required), `Missing ${required} section`);
  }
  assert.equal(await page.locator('script').count(), 0);
  assert.equal(networkRequests.length, 0);
  assert.match(await page.locator('.repo-ref').innerText(), /Quality Studio[\s\S]*59941d8/);

  const fileName = `qs-87-completed-run-${capture.name}.png`;
  await page.screenshot({ path: resolve(output, fileName), fullPage: true });
  evidence.push({ ...capture, fileName, sections, networkRequests: networkRequests.length });
  await page.close();
}

await browser.close();
await writeFile(resolve(output, 'qs-87-run-document-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), htmlPath, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
