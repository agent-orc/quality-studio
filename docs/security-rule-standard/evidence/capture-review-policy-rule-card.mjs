// QS-131 capture: Review policy -> Rules & rationale, Security kind, QS-GN-001 open, light and dark.
// Usage: node capture-review-policy-rule-card.mjs <quality-studio checkout with a Release API build> <output dir>
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
const repositoryRoot = process.argv[2];
const outDir = process.argv[3];
const { chromium } = await import(resolve(repositoryRoot, 'frontend/node_modules/playwright-core/index.mjs'));
const frontendRoot = resolve(repositoryRoot, 'frontend');
const apiDll = resolve(repositoryRoot, 'backend/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const children = []; const pageErrors = [];
try {
  await mkdir(outDir, { recursive: true });
  const tempRoot = await mkdtemp(resolve(tmpdir(), 'qs131-capture-'));
  await mkdir(resolve(tempRoot, '.quality-studio'), { recursive: true });
  await writeFile(resolve(tempRoot, '.quality-studio/repositories.json'), JSON.stringify([{
    id: 'default', displayName: 'Quality Studio', rootPath: repositoryRoot, globalInputsDirectory: null, inputBudgetCharacters: 12000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null }], null, 2));
  const apiPort = await freePort(); const webPort = await freePort();
  start('dotnet', [apiDll, '--urls', `http://127.0.0.1:${apiPort}`, '--contentRoot', tempRoot], tempRoot, {
    QualityStudio__RepositoryRoot: repositoryRoot, QualityStudio__AllowedRoots__0: resolve(repositoryRoot, '..'),
    QualityStudio__Security__Mode: 'Local', QualityStudio__DataRoot: resolve(tempRoot, 'data'),
    QualityStudio__AllowedOrigins__0: `http://127.0.0.1:${webPort}` });
  await waitForHttp(`http://127.0.0.1:${apiPort}/health`, 60_000);
  const proxyPath = resolve(tempRoot, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({ '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: false } }));
  start(process.execPath, [resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js'), 'serve', '--host', '127.0.0.1', '--port', String(webPort), '--proxy-config', proxyPath], frontendRoot, { NG_CLI_ANALYTICS: 'false' });
  await waitForHttp(`http://127.0.0.1:${webPort}`, 240_000);
  const browser = await chromium.launch({ executablePath: process.env.CHROME_BIN || chromium.executablePath(), headless: true, args: ['--no-sandbox'] });
  try {
    for (const theme of ['light', 'dark']) {
      const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, colorScheme: theme, deviceScaleFactor: 1 });
      page.on('pageerror', e => pageErrors.push(`${theme}: ${e.message}`));
      await page.goto(`http://127.0.0.1:${webPort}/?theme=${theme}&repo=default&path=.`, { waitUntil: 'networkidle' });
      const policyButton = page.getByRole('button', { name: 'Review policy', exact: true });
      await policyButton.waitFor({ state: 'visible', timeout: 60_000 });
      await page.waitForFunction(() => [...document.querySelectorAll('button.guidelines-button')].some(b => b.textContent.trim() === 'Review policy' && !b.disabled), null, { timeout: 60_000 });
      await policyButton.click();
      const kind = page.locator('.criteria-filters select').first();
      await kind.waitFor({ state: 'visible', timeout: 30_000 });
      await kind.selectOption('security');
      await page.locator('.rule-search input').fill('QS-GN-001');
      const card = page.locator('details.rule-card[data-rule-id="QS-GN-001"]');
      await card.waitFor({ state: 'visible', timeout: 30_000 });
      await card.locator('summary').first().click();
      await delay(600);
      await card.scrollIntoViewIfNeeded();
      await delay(300);
      const dialog = page.locator('[role="dialog"]').first();
      const target = (await dialog.count()) ? dialog : page;
      await target.screenshot({ path: resolve(outDir, `qs-review-policy-security-rule-card-${theme}--real.png`) });
      await page.close();
    }
  } finally { await browser.close(); }
  console.log(JSON.stringify({ ok: true, pageErrors }));
} catch (error) {
  console.error('capture failed', error); console.log(JSON.stringify({ ok: false, pageErrors })); process.exitCode = 1;
} finally { for (const c of children) { try { c.kill('SIGTERM'); } catch {} } await delay(1500); for (const c of children) { try { c.kill('SIGKILL'); } catch {} } }
function start(command, args, cwd, env) { const c = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'ignore', 'inherit'] }); children.push(c); return c; }
async function freePort() { const s = createServer(); await new Promise((r, j) => s.listen(0, '127.0.0.1', r).once('error', j)); const p = s.address().port; await new Promise(r => s.close(r)); return p; }
async function waitForHttp(url, t) { const end = Date.now() + t; while (Date.now() < end) { try { if ((await fetch(url)).ok) return; } catch {} await delay(500); } throw new Error('timeout ' + url); }
