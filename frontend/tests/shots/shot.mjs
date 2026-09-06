import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? 'results');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
await page.goto(process.env.QS_URL ?? 'http://127.0.0.1:4201/?theme=dark');
await page.getByRole('tab', { name: /code/i }).waitFor();
await page.screenshot({ path: join(output, 'shell-componentized--real.png'), fullPage: true });

// Exercise explorer -> editor -> review-panel wiring across the new components.
await page.getByRole('button', { name: /ApiContracts\.cs/ }).click();
await page.getByRole('tab', { name: /security/i }).click().catch(() => {});
await page.screenshot({ path: join(output, 'file-selected-security-aspect--real.png'), fullPage: true });

await browser.close();
console.log('done');
