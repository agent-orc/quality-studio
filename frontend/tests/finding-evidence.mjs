import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const fingerprint = `sha256:${'b'.repeat(64)}`;
const suppression = {
  schemaVersion: 1,
  revision: 3,
  rules: [{
    id: `exact-${'b'.repeat(64)}`,
    enabled: true,
    match: { fingerprint },
    effect: 'suppress',
    reason: 'Generated sample is retained for compatibility evidence.',
    author: 'Reviewer',
    createdAt: '2026-08-12T20:00:00Z',
    expiresAt: null,
    path: 'src/QualityStudio.Api/Program.cs',
    ruleId: 'generic-api-key',
    title: 'Hard-coded API token',
  }],
};

await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = [];

for (const theme of ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => localStorage.setItem('qs-layout', JSON.stringify({
    explorerVisible: true, reviewVisible: true, explorerWidth: 280, reviewWidth: 520,
  })));
  await page.route(/\/api\/repos\/[^/]+\/(?:tree|file)(?:\?|$)/, route => route.abort());
  await page.route(/\/api\/(?:repos\/[^/]+\/)?findings\/suppressions(?:\?|$)/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(suppression),
  }));

  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.locator('.finding-card').first().click();
  const selectedSpans = page.locator('.finding-span.selected');
  await selectedSpans.first().waitFor();
  const spanAudit = await page.evaluate(() => ({
    selectedSpanCount: document.querySelectorAll('.finding-span.selected').length,
    selectedText: [...document.querySelectorAll('.finding-span.selected')].map(node => node.textContent).join('\n'),
    columns: [...document.querySelectorAll('.finding-span.selected')].map(node => node.getAttribute('data-finding-columns')),
    wholeSelectedLineCount: document.querySelectorAll('.code-line.selected-range').length,
    focusableSpanCount: [...document.querySelectorAll('.finding-span.selected')]
      .filter(node => node instanceof HTMLElement && node.tabIndex === 0).length,
  }));
  if (spanAudit.selectedSpanCount < 1 || spanAudit.focusableSpanCount !== spanAudit.selectedSpanCount)
    throw new Error(`${theme}: exact finding spans are not visible and keyboard focusable`);

  const exactScreenshot = `qs-84-after-exact-spans-${theme}.png`;
  await page.screenshot({ path: join(output, exactScreenshot), fullPage: true });

  await page.getByRole('button', { name: /Ignore list/ }).click();
  const ignoreManager = page.locator('.ignore-manager');
  await ignoreManager.waitFor();
  await ignoreManager.scrollIntoViewIfNeeded();
  const ignoreAudit = await ignoreManager.evaluate(node => ({
    ruleCount: node.querySelectorAll('.ignore-rule-list article').length,
    hasPersistenceCopy: node.textContent?.includes('survive review runs') ?? false,
    hasReason: node.textContent?.includes('Generated sample is retained') ?? false,
    hasRemove: [...node.querySelectorAll('button')].some(button => button.textContent?.includes('Remove')),
  }));
  if (ignoreAudit.ruleCount !== 1 || !ignoreAudit.hasPersistenceCopy || !ignoreAudit.hasReason || !ignoreAudit.hasRemove)
    throw new Error(`${theme}: Ignore list evidence is incomplete`);
  const ignoreScreenshot = `qs-84-after-ignore-list-${theme}.png`;
  await page.locator('.review-pane').screenshot({ path: join(output, ignoreScreenshot) });

  evidence.push({ theme, exactScreenshot, ignoreScreenshot, spanAudit, ignoreAudit,
    dataSources: { application: 'running QS-84 build', treeAndFile: 'documented preview fallback', ignoreList: 'deterministic API fixture' } });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-84-finding-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
