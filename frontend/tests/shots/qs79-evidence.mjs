import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const prefix = process.argv[3] ?? 'qs-79';
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });

const viewports = {
  wide: { width: 1600, height: 1000 },
  narrow: { width: 480, height: 900 },
};

for (const [size, viewport] of Object.entries(viewports)) {
  for (const theme of ['light', 'dark']) {
    const page = await browser.newPage({ viewport, deviceScaleFactor: 1 });
    const url = new URL(baseUrl);
    url.searchParams.set('theme', theme);
    url.searchParams.set('path', 'backend/src/QualityStudio.Api/Program.cs');
    await page.goto(url.toString());
    await page.locator('[data-connection-state="live"]').waitFor();
    const picker = page.locator('.scope-review-launcher [aria-label="Review model"]');
    await picker.waitFor();
    await picker.focus();
    const menu = page.locator('.scope-review-launcher .model-options');
    await menu.waitFor();
    await page.waitForTimeout(150);
    await page.screenshot({ path: join(output, `${prefix}-${size}-${theme}-open.png`), fullPage: false });
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
    await page.screenshot({ path: join(output, `${prefix}-${size}-${theme}-closed.png`), fullPage: false });
    await page.close();
  }
}

await browser.close();
console.log(JSON.stringify({ output, prefix }, null, 2));
