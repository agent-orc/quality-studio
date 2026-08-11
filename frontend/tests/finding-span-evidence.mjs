import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
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
    impact: 'The returned value can differ from the expected value.',
    evidenceItems: [{ id: 'source-1', class: 'source-span', status: 'observed', summary: 'Captured exact source span.', anchorIndex: 0 }],
    reproduction: { status: 'specified', steps: ['Call Compose.'] },
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', role: 'primary', range: { start: { line: 3, column: 9 }, end: { line: 4, column: 17 } } }],
  },
  {
    id: 'unicode-overlap', fingerprint: `sha256:${'2'.repeat(64)}`, ruleId: 'proof/unicode-overlap',
    aspect: 'maintainability', severity: 'medium', title: 'Unicode overlap span',
    description: 'The range includes a non-BMP character and overlaps the first finding.', recommendation: 'Use UTF-16-aligned columns.',
    impact: 'A wrong offset would highlight unrelated code.',
    evidenceItems: [{ id: 'source-2', class: 'source-span', status: 'observed', summary: 'Captured Unicode source span.', anchorIndex: 0 }],
    reproduction: { status: 'not-applicable', steps: [], reason: 'Presentation evidence.' },
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', role: 'primary', range: { start: { line: 3, column: 17 }, end: { line: 3, column: 22 } } }],
  },
  {
    id: 'end-of-line-span', fingerprint: `sha256:${'3'.repeat(64)}`, ruleId: 'proof/eol',
    aspect: 'maintainability', severity: 'low', title: 'End-of-line span',
    description: 'A zero-width end-of-line anchor remains visible.', recommendation: 'Keep the explicit EOL marker.',
    impact: 'Zero-width anchors could otherwise disappear.',
    evidenceItems: [{ id: 'source-3', class: 'source-span', status: 'observed', summary: 'Captured EOL anchor.', anchorIndex: 0 }],
    reproduction: { status: 'not-applicable', steps: [], reason: 'Presentation evidence.' },
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', role: 'primary', range: { start: { line: 4, column: 25 }, end: { line: 4, column: 25 } } }],
  },
];
const meta = {
  schemaVersion: 3,
  reviewedAt: '2026-08-11T18:00:00.000Z',
  kind: 'code',
  reviewer: { agent: 'evidence-runner', model: 'deterministic', thinkingLevel: 'none' },
  grade: { score: 82, band: 'B', rationale: 'Exact span evidence fixture.' },
  summary: 'Exact, overlapping, Unicode, multi-line, and end-of-line range evidence.',
  findings,
};

const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = [];
for (const theme of ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      path: 'src/QualityStudio.Api/Program.cs', content: source, metaDocuments: [meta],
      sizeBytes: Buffer.byteLength(source), lineEnding: 'lf', encoding: 'utf-8',
    }),
  }));
  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.locator('.finding-span').first().waitFor();
  await page.locator('.finding-card').filter({ hasText: 'Multi-line exact span' }).click();
  await page.locator('.finding-span.selected').first().waitFor();
  await page.locator('.finding-span.overlap').first().click();
  await page.locator('.finding-overlap-chooser').waitFor();

  const audit = await page.evaluate(() => ({
    exactSegments: document.querySelectorAll('.finding-span').length,
    selectedSegments: document.querySelectorAll('.finding-span.selected').length,
    overlappingSegments: document.querySelectorAll('.finding-span.overlap').length,
    eolMarkers: document.querySelectorAll('.finding-span.end-of-line').length,
    chooserOptions: document.querySelectorAll('.finding-overlap-chooser [role="option"]').length,
    rangeLabels: [...document.querySelectorAll('.finding-span')].map(node => node.getAttribute('aria-label')),
  }));
  if (audit.exactSegments < 3 || audit.selectedSegments < 2 || audit.overlappingSegments < 1 ||
      audit.eolMarkers !== 1 || audit.chooserOptions !== 2 ||
      !audit.rangeLabels.some(label => label?.includes('columns 17 through 22'))) {
    throw new Error(`${theme}: exact-span presentation audit failed: ${JSON.stringify(audit)}`);
  }
  const screenshot = `qs-70-exact-finding-spans-${theme}.png`;
  await page.screenshot({ path: join(output, screenshot), fullPage: true });
  evidence.push({ theme, screenshot, audit });
  await page.close();
}
await browser.close();

await writeFile(join(output, 'qs-70-exact-finding-spans.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), source, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
