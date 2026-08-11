import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';
import { chromium } from '../frontend/node_modules/playwright-core/index.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const frontendRoot = resolve(repositoryRoot, 'frontend');
const apiDll = process.env.QS_API_DLL ||
  resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const agentStudioRoot = process.env.QS_AGENT_STUDIO_REPOSITORY || '/home/agent/runner-work/PROJ-002/repo';
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const browserPath = process.env.CHROME_BIN || chromium.executablePath();
const tempRoot = await mkdtemp(resolve(tmpdir(), 'qs-agent-studio-browser-'));
const apiHost = resolve(tempRoot, 'api-host');
const children = [];

try {
  await mkdir(resolve(apiHost, '.quality-studio'), { recursive: true });
  await mkdir(resultsRoot, { recursive: true });
  const registration = (id, displayName, rootPath) => ({
    id, displayName, rootPath, globalInputsDirectory: null, inputBudgetCharacters: 12_000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100_000, defaultReviewCostCap: null,
  });
  await writeFile(resolve(apiHost, '.quality-studio', 'repositories.json'), JSON.stringify([
    registration('default', 'Quality Studio', repositoryRoot),
    registration('agent-studio', 'Agent Studio', agentStudioRoot),
  ], null, 2));

  const apiPort = await freePort();
  const webPort = await freePort();
  const proxyPath = resolve(tempRoot, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: true },
  }));
  let api = await startApi(apiPort);
  const ngCli = resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js');
  start('web', process.execPath,
    [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(webPort), '--proxy-config', proxyPath],
    frontendRoot, {}, []);
  await waitForHttp(`http://127.0.0.1:${webPort}`, 90_000);
  await waitFor(() => api.lines.some(line => line.includes('"event":"qs.repository.prewarm"') &&
    line.includes('"repositoryId":"agent-studio"')), 90_000, 'Agent Studio prewarm');

  const browser = await chromium.launch({
    executablePath: browserPath,
    headless: true,
    args: process.platform === 'linux' ? ['--no-sandbox'] : [],
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const events = [];
    page.on('console', message => {
      try {
        const event = JSON.parse(message.text());
        if (event.event?.startsWith('qs.')) events.push(event);
      } catch { /* Angular diagnostics are not product timing events. */ }
    });
    await page.goto(`http://127.0.0.1:${webPort}/?theme=light&repo=default&path=.`);
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });

    await switchRepository(page, 'Agent Studio');
    await waitForMeasure(page, 1);
    await page.waitForTimeout(5_000);
    await switchRepository(page, 'Quality Studio');
    await waitForMeasure(page, 2);
    await page.waitForTimeout(2_000);
    await switchRepository(page, 'Agent Studio');
    await waitForMeasure(page, 3);

    await page.locator('.repository-trigger').click();
    const agentMenuItem = page.getByRole('menuitemradio', { name: /Agent Studio/i });
    const pathText = await agentMenuItem.locator('small').textContent();
    const pathTitle = await agentMenuItem.locator('small').getAttribute('title');
    await page.screenshot({ path: resolve(resultsRoot, 'agent-studio-switcher-full-path.png') });
    await page.keyboard.press('Escape');

    await page.reload();
    await page.locator('.repository-trigger').filter({ hasText: 'Agent Studio' }).waitFor({ state: 'visible' });
    const restoredRepositoryId = await page.evaluate(() => localStorage.getItem('qs-last-repository'));

    await stop(api.child);
    await page.locator('.tree-pane .quiet').click();
    await page.locator('.api-notice').filter({ hasText: 'API unavailable' }).waitFor({ state: 'visible' });
    await page.screenshot({ path: resolve(resultsRoot, 'agent-studio-api-down-notice.png') });

    api = await startApi(apiPort);
    await page.getByRole('button', { name: 'Retry' }).click();
    await page.locator('.api-notice').waitFor({ state: 'detached' });

    const usable = events.filter(event => event.event === 'qs.repository.switch.usable');
    const transitions = events.filter(event => event.event === 'qs.repository.transition-visible');
    const result = {
      measuredAt: new Date().toISOString(),
      repository: {
        label: 'Agent Studio',
        root: agentStudioRoot,
        head: command('git', ['-C', agentStudioRoot, 'rev-parse', 'HEAD']),
        trackedFiles: Number(command('git', ['-C', agentStudioRoot, 'ls-files']).split('\n').filter(Boolean).length),
      },
      coldAgentStudioSwitch: usable[0] ?? null,
      warmAgentStudioSwitch: usable[2] ?? null,
      transitionEvents: transitions,
      usableEvents: usable,
      lastProject: { restoredRepositoryId, visibleName: 'Agent Studio' },
      apiDown: { noticeVisible: true, retryRecovered: true },
      switcherPath: { rendered: pathText?.trim(), tooltip: pathTitle },
    };
    await writeFile(resolve(resultsRoot, 'agent-studio-browser-switch.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
    if (!result.warmAgentStudioSwitch || result.warmAgentStudioSwitch.durationMs >= 100 ||
        restoredRepositoryId !== 'agent-studio' || pathText?.trim() !== agentStudioRoot || pathTitle !== agentStudioRoot) {
      process.exitCode = 1;
    }
  } finally {
    await browser.close();
  }
} finally {
  await Promise.allSettled(children.map(child => stop(child)));
  await rm(tempRoot, { recursive: true, force: true });
}

async function startApi(port) {
  const lines = [];
  const child = start('api', 'dotnet',
    [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', apiHost],
    apiHost,
    {
      QualityStudio__RepositoryRoot: repositoryRoot,
      QualityStudio__AllowedRoots__0: '/home/agent/runner-work',
      QualityStudio__Security__Mode: 'Local',
    },
    lines);
  await waitForHttp(`http://127.0.0.1:${port}/health`, 90_000);
  return { child, lines };
}

function start(name, commandName, arguments_, cwd, extraEnvironment, lines) {
  const child = spawn(commandName, arguments_, {
    cwd,
    env: { ...process.env, ...extraEnvironment },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  for (const stream of [child.stdout, child.stderr]) collectLines(stream, lines);
  child.once('exit', code => {
    if (code && !child.killed) console.error(`${name} exited with code ${code}`);
  });
  return child;
}

function collectLines(stream, lines) {
  stream.setEncoding('utf8');
  let buffer = '';
  stream.on('data', chunk => {
    buffer += chunk;
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      lines.push(buffer.slice(0, newline));
      buffer = buffer.slice(newline + 1);
    }
  });
}

async function switchRepository(page, name) {
  await page.locator('.repository-trigger').click();
  await page.getByRole('menuitemradio', { name: new RegExp(name, 'i') }).click();
}

async function waitForMeasure(page, count) {
  await page.waitForFunction(expected =>
    performance.getEntriesByName('qs.repository.switch.usable').length >= expected, count);
}

async function waitForHttp(url, timeoutMs) {
  await waitFor(async () => {
    try { return (await fetch(url)).ok; } catch { return false; }
  }, timeoutMs, url);
}

async function waitFor(predicate, timeoutMs, label) {
  const started = performance.now();
  while (performance.now() - started < timeoutMs) {
    if (await predicate()) return;
    await delay(50);
  }
  throw new Error(`Timed out waiting for ${label}`);
}

async function stop(child) {
  if (child.exitCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(resolvePromise => child.once('exit', resolvePromise)), delay(5_000)]);
  if (child.exitCode === null) child.kill('SIGKILL');
}

async function freePort() {
  const server = createServer();
  await new Promise((resolvePromise, rejectPromise) => server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
}

function command(executable, arguments_) {
  const result = spawnSync(executable, arguments_, { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`${executable} failed: ${result.stderr}`);
  return result.stdout.trim();
}
