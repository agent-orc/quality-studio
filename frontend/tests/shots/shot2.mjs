import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? 'results');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
await page.goto(process.env.QS_URL ?? 'http://127.0.0.1:4201/?theme=dark');
await page.waitForTimeout(2500);
await page.screenshot({ path: join(output, 'shell-componentized--real.png'), fullPage: true });

// Exercise qs-explorer's toggle output and fileOpen output against the live tree.
await page.locator('.tree-row').filter({ hasText: 'src' }).first().click();
await page.waitForTimeout(300);
await page.locator('.tree-row').filter({ hasText: 'AgentOrchestrator.CodeQuality' }).first().click();
await page.waitForTimeout(300);
await page.locator('.tree-row').filter({ hasText: 'ReviewRunner.cs' }).first().click();
await page.waitForTimeout(800);
await page.screenshot({ path: join(output, 'explorer-expanded-file-open--real.png'), fullPage: true });

await browser.close();
console.log('done');
