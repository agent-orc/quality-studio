import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.env.JOB_RESULTS_DIR ?? 'results');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const path = 'src/A.cs';
const source = Array.from({ length: 30 }, (_, index) => index === 20
  ? '    await reader.ReadAsync(buffer, cancellationToken);'
  : `// Example source line ${index + 1}`).join('\n');
const finding = {
  id: 'ca2016-example', fingerprint: `sha256:${'a'.repeat(64)}`, ruleId: 'CA2016',
  aspect: 'analyzer', severity: 'medium', title: 'Forward the CancellationToken parameter to methods',
  description: 'Forward the cancellationToken parameter to ReadAsync.', recommendation: 'Pass the token.',
  locations: [{ path, range: { start: { line: 21, column: 5 }, end: { line: 21, column: 30 } } }],
  source: { kind: 'deterministic', sensorId: 'roslyn', producer: 'Microsoft.CodeAnalysis' },
};
const scannedAt = '2026-09-28T08:00:00Z';
const node = (id, name, level, nodePath, children = []) => ({
  id, name, level, path: nodePath, kinds: {}, children, hasChildren: children.length > 0,
});
const tree = [node('repo', 'Example repository', 'repository', '.', [
  node('src', 'src', 'folder', 'src', [node('file', 'A.cs', 'file', path)]),
])];
const file = {
  path, content: source, metaDocuments: [], sizeBytes: Buffer.byteLength(source),
  lineEnding: 'lf', encoding: 'utf-8', analyzers: {
    sensors: [{
      sensorId: 'roslyn', available: true, unavailableReason: null, scannedAt,
      scope: 'repository', target: '.', findings: 1, suppressedFindings: 2, toolVersions: {},
      lastAttempt: { available: true, unavailableReason: null, scannedAt, scope: 'repository', target: '.' },
    }],
    findings: [{ sensorId: 'roslyn', finding, catalogueRules: [{
      id: 'QS-CS-003', title: 'Propagate CancellationToken; never write async void',
      technology: 'dotnet', enabled: true, severity: 'high',
    }] }],
  },
};
const repository = {
  id: 'example', displayName: 'Example repository', rootPath: '/example', globalInputsDirectory: null,
  inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'],
  archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
};

await mkdir(output, { recursive: true });
const browser = await chromium.launch({
  executablePath: process.env.CHROME_BIN || chromium.executablePath(),
  headless: true, args: ['--no-sandbox'],
});
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 }, deviceScaleFactor: 1 });
  await page.addInitScript(() => localStorage.setItem('qs-layout', JSON.stringify({
    explorerVisible: true, reviewVisible: false, explorerWidth: 320, reviewWidth: 440,
  })));
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const endpoint = url.pathname;
    let body;
    if (endpoint === '/api/repos') body = { repositories: [repository], defaultRepositoryId: repository.id };
    else if (endpoint === '/api/repos/example/tree/v2') body = {
      schemaVersion: 2, parentId: null, path: '.', offset: 0, limit: 500, nextCursor: null, nodes: tree,
    };
    else if (endpoint === '/api/repos/example/analyzers/counts') body = { files: { [path]: 1 } };
    else if (endpoint === '/api/repos/example/file') body = file;
    else if (endpoint === '/api/repos/example/review/runs') body = { runs: [] };
    else if (endpoint === '/api/quotas') body = { at: scannedAt, ttlSeconds: 60, providers: [] };
    if (body === undefined) return route.fulfill({ status: 404 });
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.goto(new URL(`?repo=example&path=${encodeURIComponent(path)}&kind=code`, baseUrl).toString());
  await page.locator('.analyzer-badge').first().waitFor();
  await page.locator('.analyzer-strip').waitFor();
  await page.locator('.analyzer-strip summary').click();
  await page.locator('.catalogue-rule').waitFor();
  const checks = {
    explorerBadges: await page.locator('.analyzer-badge').count(),
    editorRule: await page.locator('.catalogue-rule').first().innerText(),
    editorDiagnostic: await page.locator('.analyzer-list code').first().innerText(),
  };
  if (checks.explorerBadges < 2 || checks.editorRule !== 'QS-CS-003' || !checks.editorDiagnostic.includes('CA2016'))
    throw new Error(`Analyzer UI evidence incomplete: ${JSON.stringify(checks)}`);
  await page.screenshot({ path: join(output, 'qs-113-analyzer-explorer-editor.png') });
  await writeFile(join(output, 'qs-113-analyzer-ui-evidence.json'), `${JSON.stringify({
    fixture: 'mocked persisted API response', url: page.url(), checks,
  }, null, 2)}\n`);
  console.log(JSON.stringify(checks));
} finally {
  await browser.close();
}
