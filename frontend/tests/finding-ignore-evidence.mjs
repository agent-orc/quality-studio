import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const fingerprint = `sha256:${'4'.repeat(64)}`;
const path = 'src/QualityStudio.Api/Program.cs';
const source = ['public string Compose()', '{', '    return "review evidence";', '}'].join('\n');

await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = [];

for (const theme of ['light', 'dark']) {
  let stored = { schemaVersion: 1, revision: 0, rules: [] };
  let savedRequest = null;
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });

  await page.route(/\/api\/(?:repos\/[^/]+\/)?file(?:\?|$)/, route => {
    const suppression = stored.rules[0] ?? null;
    const finding = {
      id: 'persistent-ignore',
      fingerprint,
      ruleId: 'proof/persistent-ignore',
      aspect: 'maintainability',
      severity: 'medium',
      title: 'Persistent ignore-list finding',
      description: 'The finding remains observable while a suppression controls its default presentation.',
      recommendation: 'Keep the suppression separate from assessment and resolution.',
      impact: 'Deleting an ignored observation would corrupt review history.',
      evidenceItems: [{ id: 'source-ignore', class: 'source-span', status: 'observed', summary: 'Captured exact source span.', anchorIndex: 0 }],
      reproduction: { status: 'specified', steps: ['Add an exact ignore rule and reopen the review.'] },
      assessment: { status: 'unassessed', assessedBy: null, reason: null, assessedAt: null },
      resolution: { status: 'open', taskKey: null, resolvedAt: null },
      suppressed: !!suppression,
      suppression,
      locations: [{ path, role: 'primary', range: { start: { line: 3, column: 12 }, end: { line: 3, column: 26 } } }],
    };
    return route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        path,
        content: source,
        metaDocuments: [{
          schemaVersion: 3,
          reviewedAt: '2026-08-12T03:50:00.000Z',
          kind: 'code',
          reviewer: { agent: 'evidence-runner', model: 'deterministic', thinkingLevel: 'none' },
          grade: { score: 90, band: 'A', rationale: 'Persistent ignore-list evidence.' },
          summary: 'Suppression remains independent from the observed finding.',
          decisionCounts: {
            unassessed: 1, confirmed: 0, dismissed: 0, disputed: 0,
            open: 1, planned: 0, fixed: 0, riskAccepted: 0, obsolete: 0,
            suppressed: suppression ? 1 : 0,
          },
          findings: [finding],
        }],
        sizeBytes: Buffer.byteLength(source),
        lineEnding: 'lf',
        encoding: 'utf-8',
      }),
    });
  });

  await page.route(/\/api\/(?:repos\/[^/]+\/)?findings\/suppressions(?:\/preview|\/[^/?]+)?(?:\?|$)/, async route => {
    const request = route.request();
    if (request.method() === 'GET') {
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify(stored) });
    }
    if (request.method() === 'DELETE') {
      stored = { schemaVersion: 1, revision: stored.revision + 1, rules: [] };
      return route.fulfill({ contentType: 'application/json', body: JSON.stringify(stored) });
    }

    const body = request.postDataJSON();
    const rule = {
      id: body.id,
      enabled: body.enabled,
      match: body.match,
      effect: 'suppress',
      reason: body.reason,
      author: body.author,
      createdAt: '2026-08-12T03:50:01.000Z',
      expiresAt: body.expiresAt,
    };
    if (request.url().includes('/preview')) {
      return route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          rule,
          broad: !body.match.fingerprint,
          matches: [{
            fingerprint,
            ruleId: 'proof/persistent-ignore',
            path,
            reviewKind: 'code',
            sourceKind: 'agent',
            title: 'Persistent ignore-list finding',
          }],
        }),
      });
    }

    savedRequest = body;
    stored = { schemaVersion: 1, revision: 1, rules: [rule] };
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(stored) });
  });

  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', path);
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.locator('.finding-card').filter({ hasText: 'Persistent ignore-list finding' }).click();
  const ignoreButton = page.getByRole('button', { name: 'Ignore this finding…' });
  await ignoreButton.scrollIntoViewIfNeeded();
  await ignoreButton.click();
  await page.locator('.suppression-preview').waitFor();
  await page.getByLabel('Suppression reason').fill('Known exact finding retained for audit evidence.');

  const previewScreenshot = `qs-84-ignore-list-preview-${theme}--mocked.png`;
  await page.locator('.review-pane').screenshot({ path: join(output, previewScreenshot) });
  await page.getByRole('button', { name: 'Save suppression' }).click();
  await page.getByText('Suppression saved; the observation remains available.').waitFor();
  await page.reload();
  const persistedCard = page.locator('.finding-card').filter({ hasText: 'Persistent ignore-list finding' });
  await persistedCard.click();
  const persistedLabel = page.getByText('unassessed · open · suppressed');
  await persistedLabel.waitFor();
  await persistedLabel.scrollIntoViewIfNeeded();
  const suppressedFilterLabel = await page.getByLabel('Finding state').locator('option[value="suppressed"]').textContent();
  await page.getByRole('button', { name: 'Ignore list' }).click();
  const ignoreList = page.locator('[aria-label="Finding ignore list"]');
  await ignoreList.waitFor();
  const persistedRuleCount = await ignoreList.locator('.ignore-list-rules article').count();

  const persistedScreenshot = `qs-84-ignore-list-persisted-${theme}--mocked.png`;
  await page.locator('.review-pane').screenshot({ path: join(output, persistedScreenshot) });
  const storedRevisionAtPersistence = stored.revision;
  await ignoreList.getByRole('button', { name: 'Remove' }).click();
  await ignoreList.getByText('Ignore-list rule removed; matching observations are visible again.').waitFor();
  const audit = {
    previewMatches: 1,
    storedRevisionAtPersistence,
    storedRevisionAfterRemoval: stored.revision,
    exactFingerprint: savedRequest?.match?.fingerprint,
    expectedRevision: savedRequest?.expectedRevision,
    persistedRuleCount,
    rulesAfterRemoval: stored.rules.length,
    suppressedFilterLabel,
  };
  if (audit.storedRevisionAtPersistence !== 1 || audit.storedRevisionAfterRemoval !== 2 ||
      audit.exactFingerprint !== fingerprint || audit.expectedRevision !== 0 || audit.persistedRuleCount !== 1 ||
      audit.rulesAfterRemoval !== 0 || !audit.suppressedFilterLabel?.includes('(1)')) {
    throw new Error(`${theme}: persistent ignore-list audit failed: ${JSON.stringify(audit)}`);
  }
  evidence.push({ theme, previewScreenshot, persistedScreenshot, audit });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-84-ignore-list-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
