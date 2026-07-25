import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? 'evidence');
await mkdir(output, { recursive: true });

const source = [
  'using System;',
  '',
  'namespace QualityStudio.Syntax;',
  '',
  'public sealed class HighlightEvidence',
  '{',
  '    private const string Verbatim = @"first line',
  'second line";',
  '    private const string Raw = """',
  'raw <content> & remains text',
  'raw final line',
  '""";',
  '',
  '    /* Finding decorations remain above',
  '       multiline highlighted comments. */',
  '    public bool Ready => true;',
  '}',
].join('\n');

const meta = {
  reviewedAt: '2026-07-22T10:00:00.000Z',
  kind: 'code',
  reviewer: { agent: 'evidence-runner', model: 'deterministic' },
  grade: { score: 91, band: 'A', rationale: 'Syntax evidence fixture.' },
  summary: 'Worker highlighting and finding overlays are both active.',
  findings: [{
    id: 'syntax-overlay',
    aspect: 'rendering',
    severity: 'medium',
    title: 'Finding overlay evidence',
    description: 'The finding gutter remains visible beside highlighted multiline source.',
    recommendation: 'Keep the overlay independent from token spans.',
    locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 9, column: 5 }, end: { line: 12, column: 7 } } }],
  }],
};

const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
  await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      path: 'src/QualityStudio.Api/Program.cs',
      content: source,
      metaDocuments: [meta],
      sizeBytes: Buffer.byteLength(source),
      lineEnding: 'lf',
      encoding: 'utf-8',
    }),
  }));
  await page.goto(`${process.env.QS_URL ?? 'http://127.0.0.1:4200/'}?theme=${theme}`);
  await page.locator('.tok-comment').first().waitFor();
  await page.locator('.tok-string').first().waitFor();
  await page.locator('.finding-marker').first().waitFor();

  const rendered = await page.locator('.code-line code').evaluateAll(nodes => nodes.map(node => node.textContent ?? '').join('\n'));
  if (rendered !== source) throw new Error(`${theme} token spans changed the source text`);
  const visibleRows = await page.locator('.code-line').count();
  const findingMarkers = await page.locator('.finding-marker').count();
  await page.screenshot({ path: join(output, `qs-31-syntax-${theme}.png`), fullPage: true });
  console.log(JSON.stringify({ theme, visibleRows, findingMarkers, sourceLines: source.split('\n').length }));
  await page.close();
}
await browser.close();
