import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
await mkdir(output, { recursive: true });

const source = [
  'using System;',
  '',
  'namespace QualityStudio.Api;',
  '',
  'public sealed record StartReviewRequest(',
  '    string Path,',
  '    string Kind,',
  '    string? Model = null,',
  '    string? CliType = null,',
  '    long? TokenCap = null,',
  '    decimal? CostCap = null,',
  '    bool Force = false);',
].join('\n');

const selectedFingerprint = `sha256:${'a'.repeat(64)}`;
const overlappingFingerprint = `sha256:${'b'.repeat(64)}`;

const meta = {
  reviewedAt: '2026-08-11T10:00:00.000Z',
  kind: 'code',
  reviewer: { agent: 'evidence-runner', model: 'deterministic' },
  grade: { score: 88, band: 'B', rationale: 'Exact-span evidence fixture.' },
  summary: 'Two findings intersect on the Model parameter line.',
  findings: [
    {
      id: 'finding-thinking-level', fingerprint: selectedFingerprint, ruleId: 'built-in:code', aspect: 'correctness', severity: 'high',
      title: 'Review route omits thinking level', description: 'The selected reasoning level is not passed into the review run.',
      recommendation: 'Capture requested and resolved thinking level through CliRunRequest.',
      locations: [{ path: 'backend/QualityStudio.Api/Program.cs', range: { start: { line: 8, column: 5 }, end: { line: 8, column: 17 } } }],
    },
    {
      id: 'finding-model-default', fingerprint: overlappingFingerprint, ruleId: 'built-in:code', aspect: 'maintainability', severity: 'medium',
      title: 'Model default is silently nullable', description: 'A null model falls back without recording why the request had no explicit model.',
      recommendation: 'Require an explicit model or record the fallback reason.',
      locations: [{ path: 'backend/QualityStudio.Api/Program.cs', range: { start: { line: 8, column: 13 }, end: { line: 8, column: 25 } } }],
    },
  ],
};

const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
const path = 'backend/QualityStudio.Api/Program.cs';
for (const theme of ['dark', 'light']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
  // Force the app's built-in demo dataset (any live dev-stack backend's own repository tree
  // will not contain this fixture path), then override just the file body with our fixture
  // so the two findings intersect at a known, assertable column range.
  await page.route('**/api/**', route => route.abort());
  await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      path,
      content: source,
      metaDocuments: [meta],
      sizeBytes: Buffer.byteLength(source),
      lineEnding: 'lf',
      encoding: 'utf-8',
    }),
  }));
  const url = `${process.env.QS_URL ?? 'http://127.0.0.1:4200/'}?theme=${theme}&kind=code&path=${encodeURIComponent(path)}&finding=${encodeURIComponent(selectedFingerprint)}&location=0`;
  await page.goto(url);
  await page.locator('.finding-marker').first().waitFor();
  await page.locator('.tok-selected').first().waitFor();
  await page.locator('.tok-overlap').first().waitFor();

  const rendered = await page.locator('.code-line code').evaluateAll(nodes => nodes.map(node => node.textContent ?? '').join('\n'));
  if (rendered !== source) throw new Error(`${theme} exact-span segmentation changed the source text`);

  const selectedText = await page.locator('.tok-selected').first().textContent();
  if (selectedText !== 'string? ') throw new Error(`${theme}: expected the non-overlapping selected span to read "string? ", got "${selectedText}"`);
  const overlapText = await page.locator('.tok-overlap').first().textContent();
  if (overlapText !== 'Model') throw new Error(`${theme}: expected the overlap span to read "Model", got "${overlapText}"`);

  const selectedCount = await page.locator('.tok-selected').count();
  const overlapCount = await page.locator('.tok-overlap').count();
  await page.screenshot({ path: join(output, `qs-70-exact-span-${theme}.png`), fullPage: true });
  console.log(JSON.stringify({ theme, selectedCount, overlapCount, sourceLines: source.split('\n').length }));
  await page.close();
}
await browser.close();
