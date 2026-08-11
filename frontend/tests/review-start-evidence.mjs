import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const phase = process.argv[2] ?? 'after';
const output = resolve(process.argv[3] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || (process.platform === 'win32'
  ? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
  : chromium.executablePath());
const cases = [
  { theme: 'light', width: 1600, height: 1000, size: 'wide' },
  { theme: 'dark', width: 1600, height: 1000, size: 'wide' },
  { theme: 'light', width: 960, height: 900, size: 'narrow' },
  { theme: 'dark', width: 960, height: 900, size: 'narrow' },
];

await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
const evidence = [];

for (const capture of cases) {
  const page = await browser.newPage({
    viewport: { width: capture.width, height: capture.height },
    deviceScaleFactor: 1,
  });
  const url = new URL(baseUrl);
  url.searchParams.set('theme', capture.theme);
  url.searchParams.set('path', '.');
  await page.goto(url.toString());
  await page.locator('[data-connection-state="live"]').waitFor();
  const launcher = page.locator('.scope-review-launcher');
  await launcher.waitFor();
  const fileName = `review-start-${phase}-${capture.theme}-${capture.size}.png`;
  await page.screenshot({ path: join(output, fileName), fullPage: true });
  const model = launcher.locator('[aria-label="Review model"]');
  await model.focus();
  await launcher.locator('.model-options').waitFor();

  const audit = await launcher.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const controls = [...element.querySelectorAll('select, input, button, summary')].map(control => {
      const rect = control.getBoundingClientRect();
      return { label: control.getAttribute('aria-label') ?? control.textContent?.trim() ?? '', x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    });
    return {
      width: bounds.width,
      height: bounds.height,
      scrollWidth: element.scrollWidth,
      modelLabel: element.querySelector('[role="option"]')?.textContent?.trim() ?? '',
      controls,
    };
  });
  if (phase === 'after') {
    if (!audit.modelLabel.includes('Runner default (gpt-5.6-sol)')) throw new Error(`${capture.theme}/${capture.size}: resolved model is absent`);
    if (audit.scrollWidth > Math.ceil(audit.width)) throw new Error(`${capture.theme}/${capture.size}: launcher overflows horizontally`);
  }

  const pickerFileName = `review-start-picker-${phase}-${capture.theme}-${capture.size}.png`;
  await page.screenshot({ path: join(output, pickerFileName), fullPage: true });
  let settingsFileName;
  if (phase === 'after') {
    await model.press('Escape');
    await launcher.locator('.model-options').waitFor({ state: 'hidden' });
    await launcher.locator('[aria-label="Review settings"]').click();
    await launcher.locator('.options-popover').waitFor();
    settingsFileName = `review-start-settings-${phase}-${capture.theme}-${capture.size}.png`;
    await page.screenshot({ path: join(output, settingsFileName), fullPage: true });
  }
  evidence.push({ ...capture, fileName, pickerFileName, settingsFileName, audit });
  await page.close();
}

await browser.close();
await writeFile(join(output, `review-start-${phase}-evidence.json`), `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, phase, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, phase, screenshots: evidence.flatMap(item => [item.fileName, item.pickerFileName, item.settingsFileName].filter(Boolean)) }, null, 2));
