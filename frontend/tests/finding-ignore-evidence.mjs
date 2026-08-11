import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
await mkdir(output, { recursive: true });

const source = [
  'public string Read(string path)',
  '{',
  '    return File.ReadAllText(path);',
  '}',
].join('\n');
const fingerprint = `sha256:${'b'.repeat(64)}`;
const finding = {
  id: 'finding-ignore-proof', fingerprint, ruleId: 'io/timing', aspect: 'performance', severity: 'high',
  title: 'File read has no timing evidence',
  description: 'The user-visible file read cannot be diagnosed when it is slow.',
  recommendation: 'Record a bounded duration metric.', state: 'open',
  locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: {
    start: { line: 3, column: 12 }, end: { line: 3, column: 32 },
  } }],
};
const meta = {
  reviewedAt: '2026-08-12T00:00:00.000Z', kind: 'code',
  reviewer: { agent: 'evidence-runner', model: 'deterministic' },
  grade: { score: 78, band: 'C', rationale: 'One exact finding.' },
  summary: 'Finding-level ignore evidence.', findings: [finding],
};

const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
const evidence = [];
for (const theme of ['light', 'dark']) {
  let document = { schemaVersion: 1, revision: 0, rules: [] };
  let writes = 0;
  let reads = 0;
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (/\/file$/.test(url.pathname)) {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
        path: 'src/QualityStudio.Api/Program.cs', content: source, metaDocuments: [meta],
        sizeBytes: Buffer.byteLength(source), lineEnding: 'lf', encoding: 'utf-8',
      }) });
      return;
    }
    if (/\/findings\/suppressions$/.test(url.pathname)) {
      if (request.method() === 'GET') {
        reads++;
        await route.fulfill({ contentType: 'application/json', body: JSON.stringify(document) });
        return;
      }
      if (request.method() === 'POST') {
        const body = request.postDataJSON();
        writes++;
        document = { schemaVersion: 1, revision: document.revision + 1, rules: [{
          id: 'finding-proof', enabled: true, effect: 'suppress', match: { fingerprint: body.fingerprint },
          reason: body.reason, author: body.author, createdAt: '2026-08-12T00:00:00Z', expiresAt: body.expiresAt,
        }] };
        await route.fulfill({ contentType: 'application/json', body: JSON.stringify(document) });
        return;
      }
    }
    if (/\/findings\/suppressions\/finding-proof$/.test(url.pathname) && request.method() === 'DELETE') {
      document = { schemaVersion: 1, revision: document.revision + 1, rules: [] };
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify(document) });
      return;
    }
    await route.abort();
  });

  const url = new URL(baseUrl);
  url.searchParams.set('theme', theme);
  url.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.locator('.finding-card').waitFor();
  await page.locator('.finding-card').click();
  await page.locator('.finding-span.selected').first().waitFor();
  await page.getByRole('button', { name: 'Ignore finding', exact: true }).click();
  await page.getByLabel('Ignore reason').fill('Generated compatibility surface is reviewed upstream.');
  await page.getByRole('button', { name: 'Add to ignore list', exact: true }).click();
  await page.getByText('Finding ignored. The observation remains available in the ignore list.').waitFor();
  if (await page.locator('.finding-card').count() !== 0 || await page.locator('.finding-span').count() !== 0 ||
      await page.locator('.code-line.selected-range').count() !== 0)
    throw new Error(`${theme}: ignored finding remained in the default queue or editor projection`);

  await page.reload();
  await page.getByText('1 ignored', { exact: false }).waitFor();
  await page.getByRole('button', { name: 'Ignore list (1)', exact: true }).click();
  await page.locator('.finding-card.ignored').waitFor();
  await page.locator('.finding-span').first().waitFor();
  const persisted = await page.evaluate(() => ({
    ignoredCards: document.querySelectorAll('.finding-card.ignored').length,
    exactSpans: document.querySelectorAll('.finding-span').length,
    location: document.querySelector('.finding-location')?.textContent?.trim(),
    ignoreSummary: document.querySelector('.finding-state-summary')?.textContent?.trim(),
  }));
  if (persisted.ignoredCards !== 1 || persisted.exactSpans < 1 ||
      !persisted.location?.includes(':3:12-3:32') || !persisted.ignoreSummary?.includes('Ignore list')) {
    throw new Error(`${theme}: persistent ignore-list projection failed: ${JSON.stringify(persisted)}`);
  }
  const screenshot = `after-${theme}-finding-ignore-list.png`;
  await page.screenshot({ path: join(output, screenshot), fullPage: true });

  await page.getByRole('button', { name: 'Remove from ignore list', exact: true }).click();
  await page.getByText('Finding restored to the review queue.').waitFor();
  if (document.rules.length !== 0) throw new Error(`${theme}: ignore rule was not removed`);
  evidence.push({ theme, screenshot, reads, writes, persisted });
  await page.close();
}
await browser.close();

await writeFile(join(output, 'finding-ignore-list-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), fingerprint, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
