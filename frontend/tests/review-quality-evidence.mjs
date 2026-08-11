import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { chromium } from 'playwright-core';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'results');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });

const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = {};
for (const theme of ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, colorScheme: theme });
  await page.addInitScript(() => localStorage.setItem('qs-layout', JSON.stringify({
    explorerVisible: true, reviewVisible: true, explorerWidth: 280, reviewWidth: 500,
  })));
  await page.route('**/api/**', async route => {
    if (route.request().method() === 'POST' && route.request().url().endsWith('/findings/suppressions/preview')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        matchCount: 2,
        matches: [
          { fingerprint: `sha256:${'b'.repeat(64)}`, ruleId: 'dotnet-api-safety', path: 'src/QualityStudio.Api/Program.cs', reviewKind: 'code', sourceKind: 'agent', findingId: 'route-timing', title: 'File route has no timing event' },
          { fingerprint: `sha256:${'c'.repeat(64)}`, ruleId: 'dotnet-api-safety', path: 'src/QualityStudio.Api/Routes.cs', reviewKind: 'code', sourceKind: 'agent', findingId: 'route-timing-2', title: 'Route has no timing event' },
        ],
      }) });
      return;
    }
    await route.abort();
  });
  await page.goto(`${baseUrl}/?theme=${theme}&path=${encodeURIComponent('src/QualityStudio.Api/Program.cs')}&kind=code`);
  const card = page.locator('.finding-card').first();
  await card.waitFor();
  await card.click();
  const segment = page.locator('.finding-segment.selected').first();
  await segment.waitFor();
  await segment.focus();
  await segment.press('Enter');
  const segmentAudit = await segment.evaluate(element => ({
    text: element.textContent,
    ariaLabel: element.getAttribute('aria-label'),
    selected: element.classList.contains('selected'),
    borderBottomWidth: getComputedStyle(element).borderBottomWidth,
    backgroundColor: getComputedStyle(element).backgroundColor,
  }));
  if (!segmentAudit.text || !segmentAudit.ariaLabel?.includes('columns') || !segmentAudit.selected) {
    throw new Error(`${theme} exact-span segment is not programmatically identifiable`);
  }
  await page.screenshot({ path: join(output, `qs-70-${theme}-exact-span.png`), fullPage: true });

  const detail = page.locator('.finding-detail');
  await detail.scrollIntoViewIfNeeded();
  await detail.locator('textarea[aria-label="Decision rationale"]').fill('Generated adapter scope reviewed by the owner.');
  await detail.locator('details.scope-suppression').evaluate(element => { element.open = true; });
  await detail.locator('input[aria-label="Suppression path pattern"]').fill('src/QualityStudio.Api/**');
  await detail.getByRole('button', { name: 'Preview affected findings' }).click();
  await detail.getByRole('button', { name: 'Confirm and save scope' }).waitFor();
  const previewCount = await detail.locator('.scope-preview b').textContent();
  await page.locator('.review-pane').screenshot({ path: join(output, `qs-70-${theme}-policy-preview.png`) });
  evidence[theme] = { segment: segmentAudit, suppressionPreview: previewCount };
  await page.close();
}
await browser.close();
await writeFile(join(output, 'qs-70-review-quality-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), evidence }, null, 2)}\n`);
