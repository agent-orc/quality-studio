// QS-78 operator evidence: full repository paths in the switcher, the last active project
// restored on start, and the API-offline notice bar with its retry action.
//
// Usage: node tests/qs-78-evidence.mjs [outputDirectory]
//   QS_API   base URL of a running QualityStudio.Api (default http://127.0.0.1:5199)
//   QS_URL   base URL of an already-serving frontend; when unset this script runs `ng serve`
//            with a proxy to QS_API.
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { access, mkdir, writeFile } from 'node:fs/promises';
import { constants } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(process.argv[2] ?? join(frontendRoot, 'evidence'));
const apiBase = process.env.QS_API ?? 'http://127.0.0.1:5199';
await mkdir(output, { recursive: true });

async function findBrowser() {
  const candidates = [process.env.CHROME_BIN, chromium.executablePath()].filter(Boolean);
  for (const candidate of candidates) {
    try {
      await access(candidate, constants.X_OK);
      return candidate;
    } catch {
      // Try the next supported browser location.
    }
  }
  throw new Error('No Chrome-compatible browser was found.');
}

function freePort() {
  return new Promise((resolvePort, reject) => {
    const server = createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolvePort(port));
    });
  });
}

async function waitFor(url, timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Keep polling while the dev server compiles.
    }
    await new Promise(done => setTimeout(done, 500));
  }
  throw new Error(`Timed out waiting for ${url}`);
}

const children = [];
async function serveFrontend() {
  if (process.env.QS_URL) return process.env.QS_URL.replace(/\/$/, '');
  const port = await freePort();
  const proxyPath = join(output, 'qs-78-proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({ '/api': { target: apiBase, secure: false } }, null, 2));
  const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
  children.push(spawn(process.execPath,
    [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(port), '--proxy-config', proxyPath],
    { cwd: frontendRoot, stdio: ['ignore', 'ignore', 'inherit'] }));
  const url = `http://127.0.0.1:${port}`;
  await waitFor(url);
  return url;
}

async function repositories() {
  const response = await fetch(`${apiBase}/api/repos`);
  if (!response.ok) throw new Error(`GET /api/repos returned ${response.status}`);
  return (await response.json()).repositories;
}

const results = [];
function record(check, detail) {
  results.push({ check, detail });
  console.log(`${check}: ${detail}`);
}

const url = await serveFrontend();
const browser = await chromium.launch({ executablePath: await findBrowser(), headless: true, args: ['--no-sandbox'] });
try {
  const registry = await repositories();
  // The longest registered path is the one that used to be truncated in the switcher.
  const longest = registry.reduce((a, b) => (b.rootPath.length > a.rootPath.length ? b : a));
  const other = registry.find(entry => entry.id !== longest.id) ?? longest;

  for (const theme of ['dark', 'light']) {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    await page.goto(`${url}/?theme=${theme}`);
    await page.locator('.repository-trigger').click();
    await page.locator('.repository-menu').waitFor();
    await page.screenshot({ path: join(output, `qs-78-repository-switcher-${theme}.png`), clip: { x: 0, y: 0, width: 800, height: 420 } });
    if (theme === 'dark') {
      const rendered = await page.locator('.repository-menu small').allInnerTexts();
      const missing = registry.filter(entry => !rendered.includes(entry.rootPath));
      if (missing.length) throw new Error(`Switcher hid these paths: ${missing.map(entry => entry.rootPath).join(', ')}`);
      // Truncation would leave the rendered box narrower than the text it claims to show.
      const clipped = await page.locator('.repository-menu small')
        .evaluateAll(nodes => nodes.filter(node => node.scrollWidth > node.clientWidth + 1).length);
      if (clipped) throw new Error(`${clipped} repository path(s) are still truncated.`);
      record('full-path-visible', `${registry.length} repository path(s) rendered in full, longest is ${longest.rootPath.length} characters`);

      const actions = await page.locator('.menu-actions button').evaluateAll(nodes =>
        nodes.map(node => { const box = node.getBoundingClientRect(); return { text: node.textContent.trim(), top: Math.round(box.top), height: Math.round(box.height) }; }));
      const aligned = new Set(actions.map(action => `${action.top}:${action.height}`)).size === 1;
      if (!aligned) throw new Error(`Action buttons are not aligned: ${JSON.stringify(actions)}`);
      record('actions-aligned', `${actions.map(action => action.text).join(' / ')} share top ${actions[0].top} and height ${actions[0].height}`);
    }
    await page.close();
  }

  // Last active project: switch, reload, and expect the same repository without any URL parameter.
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.goto(`${url}/`);
  await page.locator('.repository-trigger').click();
  await page.locator('.repository-menu button', { hasText: longest.displayName }).first().click();
  await page.locator('.repository-trigger strong', { hasText: longest.displayName }).waitFor();
  await page.reload();
  await page.locator('.repository-trigger strong', { hasText: longest.displayName }).waitFor({ timeout: 30_000 });
  await page.screenshot({ path: join(output, 'qs-78-last-project-restored.png'), clip: { x: 0, y: 0, width: 800, height: 120 } });
  record('last-project-restored', `plain reload reopened "${longest.displayName}" with no ?repo= parameter`);

  // API down: every request is refused mid-session, exactly as when the API process dies.
  await page.route('**/api/**', route => route.abort('connectionrefused'));
  await page.locator('.repository-trigger').click();
  await page.locator('.repository-menu button', { hasText: other.displayName }).first().click();
  const bar = page.locator('.api-offline-bar');
  await bar.waitFor({ timeout: 30_000 });
  await page.screenshot({ path: join(output, 'qs-78-api-offline.png'), clip: { x: 0, y: 0, width: 1600, height: 200 } });
  record('api-offline-visible', `notice bar reads "${(await bar.innerText()).replace(/\s+/g, ' ').trim()}"`);

  // A retry that is still failing must not throw the operator back to a fabricated default.
  await page.locator('.api-offline-bar button').click();
  await bar.waitFor();
  const stillSelected = await page.locator('.repository-trigger strong').innerText();
  if (stillSelected !== other.displayName) throw new Error(`Failed retry moved the selection to "${stillSelected}".`);
  record('failed-retry-keeps-place', `still on "${stillSelected}" after retrying against an unreachable API`);

  // Recovered connection clears the bar; the operator only has to press Retry.
  await page.unroute('**/api/**');
  await page.locator('.api-offline-bar button').click();
  await bar.waitFor({ state: 'detached', timeout: 30_000 });
  await page.screenshot({ path: join(output, 'qs-78-api-recovered.png'), clip: { x: 0, y: 0, width: 1600, height: 200 } });
  record('api-recovered', 'Retry cleared the notice bar once the API answered again');
  await page.close();

  await writeFile(join(output, 'qs-78-evidence.json'), JSON.stringify({ apiBase, url, results }, null, 2));
} finally {
  await browser.close();
  for (const child of children) child.kill('SIGTERM');
}
