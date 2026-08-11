import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { chromium } from 'playwright-core';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'results');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const expectedPath = 'src/QualityStudio.Api/Program.cs';

await mkdir(output, { recursive: true });
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: process.platform === 'linux' ? ['--no-sandbox'] : [],
});

const audits = {};
for (const theme of ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => {
    localStorage.setItem('qs-layout', JSON.stringify({
      explorerVisible: true,
      reviewVisible: true,
      explorerWidth: 280,
      reviewWidth: 460,
    }));
  });
  await page.route('**/api/**', route => route.abort());
  await page.goto(`${baseUrl}/?theme=${theme}&path=${encodeURIComponent(expectedPath)}&kind=code`);

  const firstCard = page.locator('.finding-card').first();
  await firstCard.waitFor();
  const location = (await firstCard.locator('.finding-location').textContent())?.trim();
  if (location !== `${expectedPath}:20:19`) throw new Error(`${theme}: finding card location was ${location}`);
  await firstCard.click();

  const line = page.locator('.code-line[data-line="20"]');
  const selected = line.locator('.finding-span-selected');
  await selected.first().waitFor();
  const selectedText = (await selected.allTextContents()).join('');
  if (selectedText !== 'await File.ReadAllTextAsync(path)') {
    throw new Error(`${theme}: selected span was ${JSON.stringify(selectedText)}`);
  }
  const labels = await selected.evaluateAll(elements => elements.map(element => element.getAttribute('aria-label')));
  if (labels.some(label => !label?.includes(`${expectedPath}:20:`) || !label.includes('selected'))) {
    throw new Error(`${theme}: selected spans did not expose their exact range and state`);
  }
  const detailFocused = await page.evaluate(() => document.activeElement?.classList.contains('finding-detail'));
  if (!detailFocused) throw new Error(`${theme}: finding detail did not receive programmatic focus`);

  const overlap = line.locator('.finding-span-overlap').first();
  await overlap.click();
  const chooser = page.locator('.finding-overlap-chooser');
  await chooser.waitFor();
  if (await chooser.locator('[role="menuitem"]').count() !== 2) {
    throw new Error(`${theme}: overlap chooser did not list both findings`);
  }

  const geometry = await line.evaluate(element => {
    const code = element.querySelector('code').getBoundingClientRect();
    const spans = [...element.querySelectorAll('.finding-span-selected')].map(span => span.getBoundingClientRect());
    const left = Math.min(...spans.map(span => span.left));
    const right = Math.max(...spans.map(span => span.right));
    const style = getComputedStyle(document.documentElement);
    return {
      selectedWidth: Number((right - left).toFixed(2)),
      codeWidth: Number(code.width.toFixed(2)),
      selectedBackground: getComputedStyle(element.querySelector('.finding-span-selected')).backgroundColor,
      selectedToken: style.getPropertyValue('--studio-finding-span-selected-bg').trim(),
      overlapToken: style.getPropertyValue('--studio-finding-span-overlap-line').trim(),
    };
  });
  if (!(geometry.selectedWidth > 0 && geometry.selectedWidth < geometry.codeWidth)) {
    throw new Error(`${theme}: selected geometry does not identify a narrower exact span`);
  }
  if (!geometry.selectedToken || !geometry.overlapToken) {
    throw new Error(`${theme}: semantic finding tokens are unavailable`);
  }

  await page.screenshot({ path: join(output, `qs-70-s1-${theme}.png`), fullPage: true });
  await chooser.locator('[role="menuitem"]').nth(1).click();
  await page.locator('.finding-card.selected', { hasText: 'File read bypasses repository access' }).waitFor();

  audits[theme] = { location, selectedText, labels, detailFocused, overlapChoices: 2, geometry };
  await page.close();
}

await writeFile(
  join(output, 'qs-70-s1-exact-span-audit.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), audits }, null, 2)}\n`,
);
await browser.close();
