import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const mode = process.argv[2] ?? 'after';
const output = resolve(process.argv[3] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });

const source = [
  'public string Compose()',
  '{',
  '    var emoji = "A😀B";',
  '    return emoji.Trim();',
  '}',
].join('\n');
const findings = [
  {
    id: 'multi-line-span', fingerprint: `sha256:${'1'.repeat(64)}`, ruleId: 'proof/multi-line',
    aspect: 'correctness', severity: 'high', title: 'Multi-line exact span',
    description: 'The primary evidence crosses a line boundary.', recommendation: 'Inspect only the marked source.',
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 3, column: 9 }, end: { line: 4, column: 17 } } }],
  },
  {
    id: 'unicode-overlap', fingerprint: `sha256:${'2'.repeat(64)}`, ruleId: 'proof/unicode-overlap',
    aspect: 'maintainability', severity: 'medium', title: 'Unicode overlap span',
    description: 'The range includes a non-BMP character and overlaps the first finding.', recommendation: 'Use UTF-16-aligned columns.',
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 3, column: 17 }, end: { line: 3, column: 22 } } }],
  },
  {
    id: 'end-of-line-span', fingerprint: `sha256:${'3'.repeat(64)}`, ruleId: 'proof/eol',
    aspect: 'maintainability', severity: 'low', title: 'End-of-line span',
    description: 'A zero-width end-of-line anchor remains visible.', recommendation: 'Keep the explicit EOL marker.',
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 4, column: 25 }, end: { line: 4, column: 25 } } }],
  },
];
const meta = {
  reviewedAt: '2026-08-12T18:00:00.000Z', kind: 'code',
  reviewer: { agent: 'evidence-runner', model: 'deterministic' },
  grade: { score: 82, band: 'B', rationale: `${mode === 'before' ? 'Before' : 'After'} QS-84.` },
  summary: 'Exact, overlapping, Unicode, multi-line, and end-of-line range evidence.', findings,
};

const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const audits = [];
const themes = mode === 'before' ? ['light'] : ['light', 'dark'];
for (const theme of themes) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.setDefaultTimeout(8_000);
  let ignored = false;
  const suppressionRule = { id: 'exact-111111111111', fingerprint: findings[0].fingerprint, reason: 'Known generated-code behavior.', author: 'Reviewer', createdAt: '2026-08-12T20:00:00Z' };
  await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      path: 'src/QualityStudio.Api/Program.cs', content: source,
      metaDocuments: [{ ...meta, findings: meta.findings.map((finding, index) => index === 0 && ignored
        ? { ...finding, suppressed: true, suppression: suppressionRule }
        : finding) }],
      sizeBytes: Buffer.byteLength(source), lineEnding: 'lf', encoding: 'utf-8',
    }),
  }));
  await page.route(/\/api\/(?:repos\/[^/]+\/)?findings\/suppressions(?:\/[^?]+)?(?:\?|$)/, route => {
    if (route.request().method() === 'POST') ignored = true;
    if (route.request().method() === 'DELETE') ignored = false;
    return route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ schemaVersion: 1, revision: ignored ? 1 : 0, rules: ignored ? [suppressionRule] : [] }),
    });
  });
  await page.route(/\/api\/(?:repos\/[^/]+\/)?tree(?:\?|$)/, async route => {
    const response = await route.fetch();
    const body = await response.json();
    const visit = nodes => {
      for (const node of nodes ?? []) {
        if (node.path === 'src/QualityStudio.Api/Program.cs') {
          node.kinds.code = { ...(node.kinds.code ?? {}), direct: 'fresh', descendants: 'fresh', overall: 'fresh' };
        }
        visit(node.children);
      }
    };
    visit(body.nodes);
    await route.fulfill({ response, json: body });
  });
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.locator('.finding-card').filter({ hasText: 'Multi-line exact span' }).click();
  await page.waitForTimeout(250);

  if (mode === 'after') {
    await page.locator('.finding-span').first().waitFor();
    await page.locator('.finding-span.overlap').first().click();
    await page.locator('.finding-overlap-chooser').waitFor();
  }
  const audit = await page.evaluate(() => ({
    exactSegments: document.querySelectorAll('.finding-span').length,
    selectedSegments: document.querySelectorAll('.finding-span.selected').length,
    selectedWholeLines: document.querySelectorAll('.code-line.selected-range').length,
    overlappingSegments: document.querySelectorAll('.finding-span.overlap').length,
    eolMarkers: document.querySelectorAll('.finding-span.end-of-line').length,
    chooserOptions: document.querySelectorAll('.finding-overlap-chooser [role="option"]').length,
  }));
  if (mode === 'before' && audit.exactSegments !== 0) throw new Error(`Expected label-only baseline: ${JSON.stringify(audit)}`);
  if (mode === 'after' && (audit.exactSegments < 3 || audit.selectedSegments < 2 || audit.overlappingSegments < 1 || audit.eolMarkers !== 1 || audit.chooserOptions !== 2)) {
    throw new Error(`Exact-span audit failed: ${JSON.stringify(audit)}`);
  }
  const screenshot = `${mode}-finding-spans-${theme}.png`;
  await page.screenshot({ path: join(output, screenshot), fullPage: true });
  let ignoreScreenshot = null;
  let ignoreAudit = null;
  if (mode === 'after') {
    await page.locator('.finding-overlap-chooser [role="option"]').filter({ hasText: 'Multi-line exact span' }).click();
    await page.getByRole('button', { name: 'Ignore finding…' }).click();
    await page.getByLabel('Ignore reason').fill('Known generated-code behavior.');
    await page.getByRole('button', { name: 'Add to Ignore list' }).click();
    await page.locator('.finding-card.ignored').waitFor();
    ignoreAudit = await page.evaluate(() => ({
      ignoredCards: document.querySelectorAll('.finding-card.ignored').length,
      ignoredFilterSelected: document.querySelector('select[aria-label="Finding state"]')?.value,
      exactObservationStillVisible: document.body.textContent?.includes('Multi-line exact span') ?? false,
      restoreActionVisible: [...document.querySelectorAll('button')].some(button => button.textContent?.includes('Restore finding')),
    }));
    if (ignoreAudit.ignoredCards !== 1 || ignoreAudit.ignoredFilterSelected !== 'ignored' ||
        !ignoreAudit.exactObservationStillVisible || !ignoreAudit.restoreActionVisible) {
      throw new Error(`Ignore-list UI audit failed: ${JSON.stringify(ignoreAudit)}`);
    }
    ignoreScreenshot = `after-ignore-list-${theme}.png`;
    await page.screenshot({ path: join(output, ignoreScreenshot), fullPage: true });
  }
  audits.push({ theme, screenshot, audit, ignoreScreenshot, ignoreAudit });
  await page.close();
}
await browser.close();
await writeFile(join(output, `${mode}-finding-spans.json`), `${JSON.stringify({ capturedAt: new Date().toISOString(), audits }, null, 2)}\n`);
console.log(JSON.stringify({ output, mode, audits }, null, 2));
