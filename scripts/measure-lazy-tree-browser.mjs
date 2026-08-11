import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';
import { chromium } from '../frontend/node_modules/playwright-core/index.mjs';

const repositoryRoot = resolve(new URL('..', import.meta.url).pathname);
const frontendRoot = resolve(repositoryRoot, 'frontend');
const largeRepositoryRoot = resolve(process.env.QS_PERF_REPOSITORY ?? '/home/agent/runner-work/PROJ-002/repo');
const resultsRoot = resolve(process.env.JOB_RESULTS_DIR ?? resolve(repositoryRoot, 'results'));
const apiDll = resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const children = [];
let tempRoot;

try {
  await mkdir(resultsRoot, { recursive: true });
  tempRoot = await mkdtemp(resolve(tmpdir(), 'qs-lazy-tree-browser-'));
  await mkdir(resolve(tempRoot, '.quality-studio'), { recursive: true });
  const registration = (id, displayName, rootPath) => ({
    id, displayName, rootPath, globalInputsDirectory: null, inputBudgetCharacters: 12000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
  });
  await writeFile(resolve(tempRoot, '.quality-studio/repositories.json'), JSON.stringify([
    registration('default', 'Quality Studio', repositoryRoot),
    registration('large', 'Agent Studio', largeRepositoryRoot),
  ], null, 2));
  const apiPort = await freePort();
  const webPort = await freePort();
  const apiLines = [];
  start('api', 'dotnet', [apiDll, '--urls', `http://127.0.0.1:${apiPort}`, '--contentRoot', tempRoot], tempRoot, {
    QualityStudio__RepositoryRoot: repositoryRoot,
    QualityStudio__AllowedRoots__0: '/home/agent/runner-work',
    QualityStudio__Security__Mode: 'Local',
    Logging__LogLevel__Default: 'Information',
    Logging__LogLevel__Microsoft_AspNetCore: 'Warning',
  }, apiLines);
  await waitForHttp(`http://127.0.0.1:${apiPort}/health`, 30_000);
  await waitFor(() => apiLines.some(line => line.includes('qs.repository.prewarm') && line.includes('"repositoryId":"large"')),
    180_000, 'large repository prewarm');

  const proxyPath = resolve(tempRoot, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: true },
  }));
  const ngCli = resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js');
  start('web', process.execPath,
    [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(webPort), '--proxy-config', proxyPath],
    frontendRoot, {}, []);
  await waitForHttp(`http://127.0.0.1:${webPort}`, 90_000);

  const executablePath = process.env.CHROME_BIN || chromium.executablePath();
  const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, colorScheme: 'dark' });
    const events = [];
    page.on('console', message => {
      try {
        const event = JSON.parse(message.text());
        if (event.event?.startsWith('qs.')) events.push(event);
      } catch { }
    });
    await page.goto(`http://127.0.0.1:${webPort}/?theme=dark&repo=default&path=.`, { waitUntil: 'networkidle' });
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible', timeout: 60_000 });

    for (let operation = 0; operation < 10; operation++) {
      const target = operation % 2 === 0 ? 'Agent Studio' : 'Quality Studio';
      const before = events.filter(event => event.event === 'qs.repository.switch.usable').length;
      await page.locator('.repository-trigger').click();
      await page.getByRole('menuitemradio', { name: new RegExp(target, 'i') }).click();
      await waitFor(() => events.filter(event => event.event === 'qs.repository.switch.usable').length > before,
        30_000, `switch ${operation + 1}`);
    }
    await switchTo(page, events, 'Agent Studio');

    const loadedBefore = events.filter(event => event.event === 'qs.data.tree-children-loaded').length;
    await clickFirstChevron(page);
    await waitFor(() => events.filter(event => event.event === 'qs.data.tree-children-loaded').length > loadedBefore,
      30_000, 'first lazy child page');
    await page.waitForTimeout(100);
    const toggleStart = events.filter(event => event.event === 'qs.tree.toggle').length;
    for (let index = 0; index < 6; index++) {
      await clickFirstChevron(page);
      await page.waitForTimeout(50);
    }
    await waitFor(() => events.filter(event => event.event === 'qs.tree.toggle').length >= toggleStart + 6,
      10_000, 'cached tree toggles');
    await page.screenshot({ path: resolve(resultsRoot, 'tree-transport-after-dark.png'), fullPage: true });
    await page.getByRole('button', { name: 'Switch to light theme' }).click();
    await page.waitForFunction(() => document.documentElement.dataset['theme'] === 'light');
    await page.screenshot({ path: resolve(resultsRoot, 'tree-transport-after.png'), fullPage: true });

    const switchEvents = events.filter(event => event.event === 'qs.repository.switch.usable');
    const largeSwitches = switchEvents.filter(event => event.repositoryId === undefined)
      .filter((_, index) => index % 2 === 0).map(event => event.durationMs).slice(0, 5);
    const cachedToggles = events.filter(event => event.event === 'qs.tree.toggle').slice(-6).map(event => event.durationMs);
    const result = {
      measuredAt: new Date().toISOString(),
      browser: await browser.version(),
      productCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repositoryRoot, encoding: 'utf8' }).trim(),
      target: {
        root: largeRepositoryRoot,
        commit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: largeRepositoryRoot, encoding: 'utf8' }).trim(),
        trackedFiles: execFileSync('git', ['ls-files'], { cwd: largeRepositoryRoot, encoding: 'utf8' }).trim().split('\n').filter(Boolean).length,
      },
      largeSwitch: summarize(largeSwitches),
      cachedExpand: summarize(cachedToggles),
      firstLazyLoad: events.findLast(event => event.event === 'qs.data.tree-children-loaded'),
      firstLazyExpand: events.findLast(event => event.event === 'qs.tree.expand-loaded'),
      transitionEvents: events.filter(event => event.event === 'qs.repository.transition-visible'),
      switchEvents,
      toggleEvents: events.filter(event => event.event === 'qs.tree.toggle'),
      screenshots: ['tree-transport-before.png', 'tree-transport-after.png', 'tree-transport-after-dark.png'],
    };
    await writeFile(resolve(resultsRoot, 'lazy-tree-browser.json'), `${JSON.stringify(result, null, 2)}\n`);
    console.log(JSON.stringify(result, null, 2));
  } finally {
    await browser.close();
  }
} finally {
  await Promise.allSettled(children.map(stop));
  if (tempRoot) await rm(tempRoot, { recursive: true, force: true });
}

async function switchTo(page, events, target) {
  const current = await page.locator('.repository-trigger strong').innerText();
  if (current.includes(target)) return;
  const before = events.filter(event => event.event === 'qs.repository.switch.usable').length;
  await page.locator('.repository-trigger').click();
  await page.getByRole('menuitemradio', { name: new RegExp(target, 'i') }).click();
  await waitFor(() => events.filter(event => event.event === 'qs.repository.switch.usable').length > before,
    30_000, `final switch to ${target}`);
}

async function clickFirstChevron(page) {
  await page.locator('qs-explorer .tree-row .chevron').first().evaluate(element => element.click());
}

function start(name, command, arguments_, cwd, extraEnvironment, lines) {
  const child = spawn(command, arguments_, {
    cwd, env: { ...process.env, ...extraEnvironment }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  for (const stream of [child.stdout, child.stderr]) collect(stream, lines);
  child.once('exit', code => { if (code && !child.killed) console.error(`${name} exited with code ${code}`); });
  return child;
}

function collect(stream, lines) {
  stream.setEncoding('utf8');
  let buffer = '';
  stream.on('data', chunk => {
    buffer += chunk;
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      lines.push(buffer.slice(0, newline).trim());
      buffer = buffer.slice(newline + 1);
    }
  });
}

async function stop(child) {
  if (!child || child.exitCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(resolvePromise => child.once('exit', resolvePromise)), delay(5_000)]);
  if (child.exitCode === null) child.kill('SIGKILL');
}

async function freePort() {
  const server = createServer();
  await new Promise((resolvePromise, rejectPromise) =>
    server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
}

async function waitForHttp(url, timeoutMs) {
  await waitFor(async () => { try { return (await fetch(url)).ok; } catch { return false; } }, timeoutMs, url);
}

async function waitFor(predicate, timeoutMs, label) {
  const started = performance.now();
  while (performance.now() - started < timeoutMs) {
    if (await predicate()) return;
    await delay(50);
  }
  throw new Error(`Timed out waiting for ${label}`);
}

function summarize(values) {
  const sorted = [...values].sort((left, right) => left - right);
  const percentile = quantile => sorted[Math.min(sorted.length - 1, Math.ceil(quantile * sorted.length) - 1)];
  return {
    samples: values.length,
    medianMs: round(percentile(0.5)),
    p95Ms: round(percentile(0.95)),
    minMs: round(Math.min(...values)),
    maxMs: round(Math.max(...values)),
    values,
  };
}

function round(value) { return Math.round(value * 100) / 100; }
