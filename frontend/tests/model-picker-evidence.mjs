import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || (process.platform === 'win32'
  ? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
  : chromium.executablePath());
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
const evidence = [];

for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
  await page.goto(url.toString());
  await page.locator('[data-connection-state="live"]').waitFor();
  const picker = page.locator('.file-review-actions [aria-label="Review model"]');
  await picker.waitFor();
  await picker.focus();
  const menu = page.locator('.file-review-actions .model-options');
  await menu.waitFor();

  const options = await menu.locator('[role="option"]').allTextContents();
  if (!options[0]?.includes('Runner default model')) throw new Error(`${theme}: Runner default is not the first option`);
  if (!options.some(option => option.includes('gpt-5.6-sol') && option.includes('frontier'))) {
    throw new Error(`${theme}: frontier capability annotation is missing`);
  }
  if (options.some(option => option.includes('gpt-5.5'))) throw new Error(`${theme}: unsupported model is offered`);
  await page.screenshot({ path: join(output, `qs-56-model-picker-${theme}.png`), fullPage: true });
  evidence.push({ theme, optionCount: options.length, firstOption: options[0].trim(), options: options.map(option => option.trim()) });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-56-model-picker-evidence.json'), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, screenshots: evidence.map(item => `qs-56-model-picker-${item.theme}.png`), optionCount: evidence[0]?.optionCount }, null, 2));
