import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const cachedChrome = '/home/agent/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome';
const executablePath = process.env.CHROME_BIN || (existsSync(cachedChrome) ? cachedChrome : chromium.executablePath());
await mkdir(output, { recursive: true });

const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: process.platform === 'linux' ? ['--no-sandbox'] : [],
});
const evidence = [];

try {
  for (const theme of ['light', 'dark']) {
    const page = await browser.newPage({ viewport: { width: 900, height: 600 }, deviceScaleFactor: 1 });
    const url = new URL(baseUrl);
    url.searchParams.set('theme', theme);
    await page.goto(url.toString(), { waitUntil: 'domcontentloaded' });
    await page.locator('.repository-trigger').click();
    await page.getByRole('button', { name: 'Configure repositories' }).click();

    const fields = page.locator('.repository-form-fields');
    const tokenInput = page.locator('[name="defaultReviewTokenCap"]');
    await tokenInput.fill('0.1M');
    await tokenInput.blur();
    if (await tokenInput.inputValue() !== '100k') throw new Error(`${theme}: tolerant token input did not normalize to 100k`);
    const initialScrollTop = await fields.evaluate(element => element.scrollTop);
    await page.locator('[name="displayName"]').focus();
    for (let index = 0; index < 5; index += 1) await page.keyboard.press('Tab');

    const state = await page.locator('qs-repository-dialog .repository-dialog').evaluate(dialog => {
      const footer = dialog.querySelector('footer').getBoundingClientRect();
      const scroller = dialog.querySelector('.repository-form-fields');
      const addButton = dialog.querySelector('.add-affordance');
      const tokenInput = dialog.querySelector('[name="defaultReviewTokenCap"]');
      return {
        focusedName: document.activeElement?.getAttribute('name'),
        footerBottom: footer.bottom,
        viewportHeight: innerHeight,
        scrollTop: scroller.scrollTop,
        scrollHeight: scroller.scrollHeight,
        clientHeight: scroller.clientHeight,
        addBorderStyle: getComputedStyle(addButton).borderStyle,
        tokenValue: tokenInput.value,
      };
    });

    if (state.focusedName !== 'defaultReviewCostCap') throw new Error(`${theme}: tab order did not reach the lower field`);
    if (state.scrollTop <= initialScrollTop) throw new Error(`${theme}: focused lower field did not scroll into view`);
    if (state.footerBottom > state.viewportHeight) throw new Error(`${theme}: registry footer is outside the viewport`);
    if (state.addBorderStyle !== 'solid') throw new Error(`${theme}: add affordance is not a solid ghost button`);
    if (state.tokenValue !== '100k') throw new Error(`${theme}: default token cap is not shown in scaled units`);

    await page.screenshot({ path: join(output, `repository-modal-after-${theme}--mocked.png`) });
    if (theme === 'light') await page.screenshot({ path: join(output, 'repository-modal-after--mocked.png') });

    await page.getByRole('button', { name: 'Add repository' }).click();
    await page.getByRole('heading', { name: 'Onboard a repository' }).waitFor();
    const onboardFooterVisible = await page.locator('qs-repository-dialog .repository-form footer').evaluate(footer =>
      footer.getBoundingClientRect().bottom <= innerHeight);
    if (!onboardFooterVisible) throw new Error(`${theme}: onboard footer is outside the viewport`);
    await page.screenshot({ path: join(output, `onboard-repository-after-${theme}--mocked.png`) });

    evidence.push({ theme, ...state, onboardFooterVisible });
    await page.close();
  }
} finally {
  await browser.close();
}

await writeFile(join(output, 'repository-modal-evidence.json'), `${JSON.stringify({ baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
