import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || (process.platform === 'win32'
  ? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
  : chromium.executablePath());

await mkdir(output, { recursive: true });
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: process.platform === 'linux' ? ['--no-sandbox'] : [],
});
const evidence = [];

for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => {
    localStorage.setItem('qs-layout', JSON.stringify({
      explorerVisible: true,
      reviewVisible: true,
      explorerWidth: 280,
      reviewWidth: 460,
    }));
  });
  await page.route(/\/api\/repos\/[^/]+\/(?:tree|file)(?:\?|$)/, route => route.abort());
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  const cards = page.locator('.finding-card');
  await cards.first().waitFor();
  await cards.first().click();
  await page.locator('.finding-detail').waitFor();
  await page.locator('.code-line.selected-range').first().waitFor();

  const picker = page.locator('.scope-review-launcher [aria-label="Review model"]');
  await picker.waitFor();
  await picker.focus();
  await page.locator('.scope-review-launcher .model-options').waitFor();

  const audit = await page.evaluate(() => {
    const detail = document.querySelector('.finding-detail');
    const detailStyle = getComputedStyle(detail);
    const visibleText = document.querySelector('.findings-heading small')?.textContent?.trim();
    return {
      reviewLauncherCount: document.querySelectorAll('qs-review-actions').length,
      modelOptionCount: document.querySelectorAll('.model-options [role="option"]').length,
      visibleFindingCountText: visibleText,
      selectedRangeCount: document.querySelectorAll('.code-line.selected-range').length,
      findingDetailBorderLeftWidth: detailStyle.borderLeftWidth,
      findingDetailBorderRightWidth: detailStyle.borderRightWidth,
      findingDetailBorderLeftColor: detailStyle.borderLeftColor,
      findingDetailBorderRightColor: detailStyle.borderRightColor,
    };
  });
  if (audit.reviewLauncherCount !== 1) throw new Error(`${theme}: expected exactly one review launcher`);
  if (audit.modelOptionCount < 2) throw new Error(`${theme}: model picker did not load the live catalog`);
  if (audit.selectedRangeCount < 1) throw new Error(`${theme}: selected finding range is not visible`);
  if (audit.findingDetailBorderLeftWidth !== audit.findingDetailBorderRightWidth
      || audit.findingDetailBorderLeftColor !== audit.findingDetailBorderRightColor) {
    throw new Error(`${theme}: legacy finding detail accent rail is still visible`);
  }

  const screenshot = `qs-69-integrated-review-session-${theme}.png`;
  await page.screenshot({ path: join(output, screenshot), fullPage: true });
  evidence.push({
    theme,
    screenshot,
    audit,
    dataSources: {
      applicationAndModelCatalog: 'live API',
      treeFileAndFindings: 'documented preview fallback induced by intercepting the tree and file endpoints',
    },
  });
  await page.close();
}

await browser.close();
await writeFile(
  join(output, 'qs-69-integrated-review-session-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`,
);
console.log(JSON.stringify({ output, evidence }, null, 2));
