// Drives the rule pool management flow in the running app and records screenshots and the
// resulting files as evidence. Needs the API with the built UI (or ng serve with its proxy) at
// QS_URL and the analysed repository's root at QS_SUBJECT, whose .quality/rules/ it inspects.
//
//   QS_URL=http://127.0.0.1:5127/ QS_SUBJECT=/tmp/subject node tests/rule-pool-evidence.mjs <output-dir>
import { chromium } from 'playwright-core';
import { mkdir, readFile, writeFile, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const subject = process.env.QS_SUBJECT;
if (!subject) throw new Error('Set QS_SUBJECT to the analysed repository root.');
const cachedChrome = '/home/agent/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome';
const executablePath = process.env.CHROME_BIN || (existsSync(cachedChrome) ? cachedChrome : chromium.executablePath());
await mkdir(output, { recursive: true });

const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
const steps = [];
const rules = join(subject, '.quality', 'rules');

async function shot(page, name, locator) {
  const path = join(output, `rule-pool-${name}.png`);
  await (locator ?? page).screenshot({ path, ...(locator ? {} : { fullPage: false }) });
  steps.push({ step: name, screenshot: `rule-pool-${name}.png` });
}

async function openPolicy(theme) {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1, acceptDownloads: true });
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  await page.goto(url.toString(), { waitUntil: 'domcontentloaded' });
  await page.getByRole('button', { name: 'Review policy' }).click();
  await page.locator('qs-rule-pool-manager .rule-pool').waitFor();
  return page;
}

const manager = page => page.locator('qs-rule-pool-manager');
const panel = page => manager(page).locator('.pool-panel');

try {
  const page = await openPolicy('light');
  await shot(page, '01-house-style-default', manager(page));

  // Packs per project type replace the house-style default.
  await manager(page).getByRole('button', { name: 'Packs & applicability' }).click();
  await panel(page).getByRole('checkbox').first().waitFor();
  const packLabels = panel(page).locator('.pack-option');
  for (const [title, checked] of [['Quality Studio house style', false], ['.NET service or API', true], ['Public website (SEO)', true]]) {
    const box = packLabels.filter({ hasText: title }).getByRole('checkbox');
    if ((await box.isChecked()) !== checked) await box.click();
  }
  await panel(page).getByPlaceholder('What kind of project this is').fill('Sample .NET service that also publishes its public product pages.');
  await shot(page, '02-packs-selection', manager(page));
  await panel(page).getByRole('button', { name: /Save applicability/ }).click();
  await manager(page).getByText('Applicability saved.').waitFor();
  await shot(page, '03-packs-saved', manager(page));

  // Per-rule override with reason.
  const card = page.locator('details.rule-card[data-rule-id="QS-CS-004"]');
  await card.locator(':scope > summary').click();
  const editor = card.locator('qs-rule-override-editor');
  await editor.locator('select').nth(1).selectOption('low');
  await editor.getByPlaceholder('Why this repository departs from the default').fill('Snapshot tests here share one fixture by design.');
  await editor.getByRole('button', { name: 'Save override' }).click();
  await editor.getByText('Override saved.').waitFor();
  await card.scrollIntoViewIfNeeded();
  await shot(page, '04-rule-override', card);

  // Custom rule: template, validation feedback, save.
  await manager(page).getByRole('button', { name: /^Custom rules/ }).click();
  await panel(page).getByRole('button', { name: '+ New custom rule' }).click();
  const textarea = panel(page).locator('textarea');
  const template = await textarea.inputValue();
  await textarea.fill(template.replace('## Detection', '## Notes'));
  await panel(page).getByRole('button', { name: 'Validate' }).click();
  await panel(page).getByText('This rule cannot be saved yet.').waitFor();
  await shot(page, '05-custom-rule-invalid', manager(page));
  await textarea.fill(template
    .replace('One imperative sentence naming the rule', 'Name every exported symbol after its domain concept')
    .replace('What to do, in one or two sentences.', 'Exported types and functions use the domain term from the glossary, never an implementation detail.'));
  await panel(page).getByRole('button', { name: 'Validate' }).click();
  await panel(page).getByText('Valid:').waitFor();
  await panel(page).locator('.rule-draft input[type="text"]').fill('The team glossary is now binding for public names.');
  await panel(page).getByRole('button', { name: 'Save custom rule' }).click();
  await manager(page).getByText('Custom rule TEAM-GN-001 saved.').waitFor();
  await shot(page, '06-custom-rule-saved', manager(page));

  // Export, then preview re-importing the same file in replace mode.
  await manager(page).getByRole('button', { name: 'Import / export' }).click();
  const [download] = await Promise.all([page.waitForEvent('download'), panel(page).getByRole('button', { name: 'Download rule set' }).click()]);
  const exported = join(output, 'rule-pool-exported-rule-set.json');
  await download.saveAs(exported);
  steps.push({ step: 'export', file: 'rule-pool-exported-rule-set.json', suggestedName: download.suggestedFilename() });
  await panel(page).locator('input[type="file"]').setInputFiles(exported);
  await panel(page).locator('select').selectOption('replace');
  await panel(page).getByRole('button', { name: 'Preview import' }).click();
  await panel(page).locator('.import-plan').waitFor();
  await shot(page, '07-import-preview', manager(page));

  await manager(page).getByRole('button', { name: 'Audit trail' }).click();
  await panel(page).locator('.audit-list li').first().waitFor();
  await shot(page, '08-audit-trail', manager(page));
  await page.close();

  // A hand-broken file blocks reviews and is reported, located, in the tool.
  const overridesPath = join(rules, 'overrides.json');
  const original = await readFile(overridesPath, 'utf8');
  const broken = JSON.parse(original);
  broken.overrides.push({ id: 'QS-CS-999', enabled: false, reason: 'Hand edit with a typo.' });
  await writeFile(overridesPath, JSON.stringify(broken, null, 2));
  const brokenPage = await openPolicy('dark');
  await manager(brokenPage).locator('[role="alert"]').waitFor();
  await shot(brokenPage, '09-broken-config-dark', manager(brokenPage));
  await brokenPage.close();
  await writeFile(overridesPath, original);

  const files = {
    applicability: JSON.parse(await readFile(join(rules, 'applicability.json'), 'utf8')),
    overrides: JSON.parse(await readFile(overridesPath, 'utf8')),
    customRules: await readdir(join(rules, 'custom')),
  };
  await writeFile(join(output, 'rule-pool-evidence.json'), `${JSON.stringify({ baseUrl, steps, files }, null, 2)}\n`);
  console.log(`rule pool evidence: ${steps.length} steps written to ${output}`);
} finally {
  await browser.close();
}
