import { chromium } from 'playwright-core';
import { access, mkdir } from 'node:fs/promises';
import { constants } from 'node:fs';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const candidates = [
  process.env.CHROME_BIN,
  chromium.executablePath(),
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
].filter(Boolean);
let executablePath;
for (const candidate of candidates) {
  try {
    await access(candidate, constants.X_OK);
    executablePath = candidate;
    break;
  } catch {
    // Continue to the next installed browser.
  }
}
if (!executablePath) throw new Error('No Chrome-compatible browser was found.');

await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });

await page.route('**/api/repos/preflight', async route => {
  const trustLevel = route.request().postDataJSON()?.trustLevel ?? 'untrusted';
  const operatorControlled = trustLevel === 'operator-controlled';
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      schemaVersion: 1,
      assessedAt: new Date().toISOString(),
      rootPath: '/workspace/payments-service',
      trustLevel,
      reviewAllowed: operatorControlled,
      reviewBoundary: operatorControlled
        ? 'Operator-controlled content passed the mandatory secret scan. Model review may run with the existing local trust restriction.'
        : 'Untrusted content is quarantined from model review and executable dependency sensors until isolated workers are available.',
      secrets: {
        status: 'pass', available: true, findingCount: 0,
        summary: 'No active secret findings were detected.', toolVersions: { gitleaks: '8.28.0' },
      },
      dependencies: operatorControlled
        ? {
            status: 'warn', available: true, findingCount: 2,
            summary: '2 dependency advisory finding(s) are surfaced for remediation.',
            toolVersions: { npm: '11.6.2' },
          }
        : {
            status: 'skipped', available: false, findingCount: 0,
            summary: 'Dependency commands were not executed against untrusted content without worker isolation.',
            toolVersions: {},
          },
      secretFindings: [],
      advisories: operatorControlled
        ? [
            {
              advisoryId: 'GHSA-demo-auth-cache', severity: 'high', package: '@example/auth-cache',
              version: '2.4.0', fixedVersion: '2.4.3', path: 'frontend/package-lock.json',
              advisoryUrl: 'https://github.com/advisories',
            },
            {
              advisoryId: 'GHSA-demo-parser', severity: 'moderate', package: 'example-parser',
              version: '5.1.0', fixedVersion: '5.1.2', path: 'tools/package-lock.json',
              advisoryUrl: 'https://github.com/advisories',
            },
          ]
        : [],
    }),
  });
});

const url = new URL(baseUrl);
url.searchParams.set('theme', 'dark');
await page.goto(url.toString());
await page.locator('[data-connection-state="live"]').waitFor();
await page.locator('.repository-trigger').click();
await page.getByRole('button', { name: '+ Onboard repository' }).click();
await page.getByLabel('Display name').fill('Payments service');
await page.getByLabel('Root path').fill('/workspace/payments-service');
await page.screenshot({ path: join(output, 'onboarding-before.png'), fullPage: true });
await page.getByRole('button', { name: 'Run onboarding checks' }).click();
await page.getByText('Review quarantined', { exact: true }).waitFor();
await page.screenshot({ path: join(output, 'onboarding-after.png'), fullPage: true });

await page.getByLabel('Operator-controlled').check();
await page.getByRole('button', { name: 'Run onboarding checks' }).click();
await page.getByText('Review allowed', { exact: true }).waitFor();
await page.getByText('@example/auth-cache 2.4.0', { exact: true }).waitFor();
await page.screenshot({ path: join(output, 'onboarding-after-advisories.png'), fullPage: true });

await browser.close();
console.log(JSON.stringify({
  output,
  screenshots: ['onboarding-before.png', 'onboarding-after.png', 'onboarding-after-advisories.png'],
}, null, 2));
