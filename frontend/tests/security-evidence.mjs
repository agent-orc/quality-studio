import { chromium } from 'playwright-core';
import { access, mkdir } from 'node:fs/promises';
import { constants } from 'node:fs';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? 'evidence');
await mkdir(output, { recursive: true });

const candidates = [
  process.env.CHROME_BIN,
  chromium.executablePath(),
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
].filter(Boolean);
let executablePath;
for (const candidate of candidates) {
  try {
    await access(candidate, constants.X_OK);
    executablePath = candidate;
    break;
  } catch {
    // Try the next supported browser location.
  }
}
if (!executablePath) throw new Error('No Chrome-compatible browser was found.');

const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
  await page.goto(`${process.env.QS_URL ?? 'http://127.0.0.1:4200/'}?theme=${theme}&kind=security`);
  await page.getByRole('tab', { name: /security/i }).waitFor();
  await page.getByRole('tab', { name: /security/i }).click();
  await page.locator('.merged-security').waitFor();
  await page.locator('.finding-card').filter({ hasText: 'Hard-coded API token' }).click();
  await page.locator('.finding-evidence').waitFor();
  await page.screenshot({ path: join(output, `qs-34-security-merged-${theme}.png`), fullPage: true });
  await page.close();
}
await browser.close();
