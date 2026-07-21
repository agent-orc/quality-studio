import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? 'results');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
await mkdir(output, { recursive: true });

const browser = await chromium.launch({
  executablePath: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  headless: true,
});

try {
  for (const theme of ['dark', 'light']) {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    await page.route(/\/api\/quotas(?:\?|$)/, route => route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 60, providers: [] }),
    }));
    await page.goto(`${baseUrl}?theme=${theme}`);
    const quota = page.getByText('Quota unavailable', { exact: true });
    await quota.waitFor();
    await page.locator('.topbar').screenshot({
      path: join(output, `topbar-quota-unavailable--${theme}--real.png`),
    });
    await page.close();
  }
} finally {
  await browser.close();
}
