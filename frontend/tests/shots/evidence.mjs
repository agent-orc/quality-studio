import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? 'results');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
await page.goto(process.env.QS_URL ?? 'http://127.0.0.1:4200/?theme=dark');
await page.getByRole('tab', { name: /code/i }).waitFor();

await page.locator('.finding-marker').first().click();
await page.screenshot({ path: join(output, 'file-findings-overlay--mocked.png'), fullPage: true });

await page.getByRole('tab', { name: /performance/i }).click();
await page.getByText('Stale review', { exact: true }).waitFor();
await page.screenshot({ path: join(output, 'stale-review-banner--mocked.png'), fullPage: true });

await page.locator('.tree-pane').screenshot({ path: join(output, 'tree-state-dots--mocked.png') });
await browser.close();
