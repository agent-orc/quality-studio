import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

// Evidence for the QS-84 finding ignore list (dossier QS-W2, slice S3).
//
// "before" runs against a build without this slice and must find no ignore-list surface at all;
// "after" runs against this build and must find the persistent list, its reason and expiry copy,
// and the removal control. Both stages open the same file with the same fixture, so the pair of
// screenshots differs only by the code under review.
const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const stage = process.env.QS_EVIDENCE_STAGE ?? 'after';
if (stage !== 'before' && stage !== 'after') throw new Error(`QS_EVIDENCE_STAGE must be 'before' or 'after'; got '${stage}'.`);
const executablePath = process.env.CHROME_BIN || chromium.executablePath();

const path = 'src/QualityStudio.Api/Program.cs';
const source = [
  'using System;',
  '',
  'namespace QualityStudio.Api;',
  '',
  'public sealed record StartReviewRequest(',
  '    string Path,',
  '    string Kind,',
  '    string? Model = null,',
  '    bool Force = false);',
].join('\n');

const ignoredFingerprint = `sha256:${'b'.repeat(64)}`;
const activeFingerprint = `sha256:${'a'.repeat(64)}`;
const suppressionId = `exact-${'b'.repeat(64)}`;
const suppression = {
  id: suppressionId,
  reason: 'Generated sample is retained for compatibility evidence.',
  author: 'Reviewer',
  createdAt: '2026-09-07T09:00:00Z',
  expiresAt: null,
};
const suppressions = {
  schemaVersion: 1,
  revision: 3,
  rules: [{
    ...suppression,
    enabled: true,
    match: { fingerprint: ignoredFingerprint },
    effect: 'suppress',
    path,
    ruleId: 'generic-api-key',
    title: 'Hard-coded API token',
  }],
};

const finding = (id, fingerprint, severity, title, description, line, startColumn, endColumn, extra = {}) => ({
  id, fingerprint, ruleId: 'built-in:code', aspect: 'correctness', severity, title, description,
  recommendation: 'Review it.',
  locations: [{ path, range: { start: { line, column: startColumn }, end: { line, column: endColumn } } }],
  ...extra,
});

const meta = {
  reviewedAt: '2026-09-07T09:00:00.000Z',
  kind: 'code',
  reviewer: { agent: 'evidence-runner', model: 'deterministic' },
  grade: { score: 88, band: 'B', rationale: 'Ignore-list evidence fixture.' },
  summary: 'One active finding and one finding on the ignore list.',
  findings: [
    finding('finding-active', activeFingerprint, 'high', 'Review route omits thinking level',
      'The selected reasoning level is not passed into the review run.', 8, 5, 17),
    // The API projects an active rule onto the observation; "before" builds simply ignore the field.
    finding('finding-ignored', ignoredFingerprint, 'medium', 'Hard-coded API token',
      'A generated sample retains a placeholder token.', 9, 5, 15, { suppression }),
  ],
};

await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = [];

for (const theme of ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => localStorage.setItem('qs-layout', JSON.stringify({
    explorerVisible: true, reviewVisible: true, explorerWidth: 280, reviewWidth: 520,
  })));
  // Force the app's documented preview fallback (no live backend here), then override just the
  // file and ignore-list responses. Later routes take precedence, so the catch-all goes first.
  await page.route('**/api/**', route => route.abort());
  await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      path, content: source, metaDocuments: [meta],
      sizeBytes: Buffer.byteLength(source), lineEnding: 'lf', encoding: 'utf-8',
    }),
  }));
  await page.route(/\/api\/(?:repos\/[^/]+\/)?findings\/suppressions(?:\?|$)/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(suppressions),
  }));

  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', path);
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.locator('.finding-card').first().waitFor();

  const queue = await page.evaluate(() => ({
    visibleCards: document.querySelectorAll('.finding-card').length,
    suppressedCards: document.querySelectorAll('.finding-card.suppressed').length,
    ignoreListControls: [...document.querySelectorAll('button')]
      .filter(node => node.textContent?.includes('Ignore list')).length,
    stateFilterOptions: [...document.querySelectorAll('select[aria-label="Finding state"] option')]
      .map(node => node.getAttribute('value')),
  }));

  if (stage === 'before') {
    if (queue.ignoreListControls !== 0 || queue.stateFilterOptions.includes('suppressed'))
      throw new Error(`${theme}: the before build already exposes an ignore list`);
    // Without the slice the ignored observation is indistinguishable from any other queue entry.
    if (queue.visibleCards !== 2 || queue.suppressedCards !== 0)
      throw new Error(`${theme}: expected both findings in the queue and none marked ignored, got ${JSON.stringify(queue)}`);
    const beforeScreenshot = `qs-84-before-no-ignore-list-${theme}.png`;
    await page.locator('.review-pane').screenshot({ path: join(output, beforeScreenshot) });
    evidence.push({ theme, stage, beforeScreenshot, queue });
    await page.close();
    continue;
  }

  // The ignored observation is hidden from the default queue and from source highlighting,
  // but it is not deleted: the Suppressed filter still finds it.
  if (queue.ignoreListControls !== 1) throw new Error(`${theme}: the Ignore list control is missing`);
  if (queue.visibleCards !== 1 || queue.suppressedCards !== 0)
    throw new Error(`${theme}: expected only the active finding by default, got ${JSON.stringify(queue)}`);
  if (!queue.stateFilterOptions.includes('suppressed'))
    throw new Error(`${theme}: the Suppressed filter option is missing`);

  const highlightedFingerprints = await page.evaluate(() => [...document.querySelectorAll('.finding-marker')]
    .map(node => node.getAttribute('data-finding-fingerprint')));
  if (highlightedFingerprints.includes(`sha256:${'b'.repeat(64)}`))
    throw new Error(`${theme}: an ignored finding still highlights current source`);

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
    throw new Error(`${theme}: Ignore list evidence is incomplete: ${JSON.stringify(ignoreAudit)}`);
  const ignoreScreenshot = `qs-84-after-ignore-list-${theme}.png`;
  await page.locator('.review-pane').screenshot({ path: join(output, ignoreScreenshot) });

  // Suppressed stays queryable, with the rule's author and reason on the observation itself.
  await page.selectOption('select[aria-label="Finding state"]', 'suppressed');
  const suppressedView = await page.evaluate(() => ({
    visibleCards: document.querySelectorAll('.finding-card').length,
    suppressedCards: document.querySelectorAll('.finding-card.suppressed').length,
  }));
  if (suppressedView.visibleCards !== 1 || suppressedView.suppressedCards !== 1)
    throw new Error(`${theme}: the Suppressed filter does not show the retained observation: ${JSON.stringify(suppressedView)}`);
  await page.locator('.finding-card.suppressed').first().click();
  const detail = page.locator('.finding-suppression');
  await detail.waitFor();
  const detailAudit = await detail.evaluate(node => ({
    hasRestore: [...node.querySelectorAll('button')].some(button => button.textContent?.includes('Restore finding')),
    text: node.textContent ?? '',
  }));
  if (!detailAudit.hasRestore || !detailAudit.text.includes('Reviewer'))
    throw new Error(`${theme}: the retained observation does not show its ignore rule: ${JSON.stringify(detailAudit)}`);
  const suppressedScreenshot = `qs-84-after-suppressed-observation-${theme}.png`;
  await page.locator('.review-pane').screenshot({ path: join(output, suppressedScreenshot) });

  evidence.push({ theme, stage, ignoreScreenshot, suppressedScreenshot, queue, ignoreAudit, suppressedView, detailAudit });
  await page.close();
}

await browser.close();
await writeFile(join(output, `qs-84-ignore-list-evidence-${stage}.json`),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), stage, baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
