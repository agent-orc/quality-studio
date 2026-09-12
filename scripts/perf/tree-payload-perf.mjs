import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const scriptRoot = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptRoot, '../..');
const frontendRoot = resolve(repositoryRoot, 'frontend');
const require = createRequire(resolve(frontendRoot, 'package.json'));
const { chromium } = require('playwright-core');
const targetRoot = resolve(process.env.QS_AGENT_STUDIO_REPO || resolve(repositoryRoot, '../agent-taskboard'));
const apiDll = process.env.QS_API_DLL || resolve(repositoryRoot, 'backend/QualityStudio.Api/bin/Debug/net10.0/QualityStudio.Api.dll');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const children = [];
let temporaryRoot;

if (!existsSync(targetRoot)) {
  throw new Error(`Agent Studio repository not found at ${targetRoot}. Set QS_AGENT_STUDIO_REPO to its worktree.`);
}
if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built. Expected ${apiDll}`);

try {
  temporaryRoot = await mkdtemp(resolve(tmpdir(), 'qs-tree-payload-perf-'));
  const apiHost = resolve(temporaryRoot, 'api-host');
  await Promise.all([mkdir(apiHost, { recursive: true }), mkdir(resultsRoot, { recursive: true })]);
  await writeRegistry(apiHost);

  const apiPort = await freePort();
  const webPort = await freePort();
  const proxyPath = resolve(temporaryRoot, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: true },
  }));

  const apiLines = [];
  start('api', 'dotnet', [apiDll, '--urls', `http://127.0.0.1:${apiPort}`, '--contentRoot', apiHost], apiHost, {
    QualityStudio__RepositoryRoot: repositoryRoot,
    QualityStudio__AllowedRoots__0: repositoryRoot,
    QualityStudio__AllowedRoots__1: targetRoot,
    QualityStudio__Security__Mode: 'Local',
  }, apiLines);
  await waitForHttp(`http://127.0.0.1:${apiPort}/health`, 30_000);

  const ngCli = resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js');
  start('web', process.execPath,
    [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(webPort), '--proxy-config', proxyPath],
    frontendRoot, {}, []);
  await waitForHttp(`http://127.0.0.1:${webPort}`, 60_000);
  await waitFor(() => apiLines.some(line => line.includes('qs.repository.prewarm') && line.includes('agent-studio')),
    180_000, 'Agent Studio prewarm event');

  const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const devtools = await page.context().newCDPSession(page);
    await devtools.send('Network.enable', { maxTotalBufferSize: 200_000_000, maxResourceBufferSize: 100_000_000 });
    const consoleEvents = [];
    page.on('console', message => {
      try {
        const event = JSON.parse(message.text());
        if (event.event?.startsWith('qs.')) consoleEvents.push(event);
      } catch { /* Ignore framework diagnostics. */ }
    });

    await page.goto(`http://127.0.0.1:${webPort}/?theme=light&repo=default&path=.`);
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });

    const cold = await switchAndMeasure(page, 'Agent Studio', 'agent-studio');
    await switchAndMeasure(page, 'Quality Studio', 'default');
    const warm = await switchAndMeasure(page, 'Agent Studio', 'agent-studio');

    const result = {
      measuredAt: new Date().toISOString(),
      browser: await browser.version(),
      target: {
        name: 'Agent Studio',
        repositoryId: 'agent-studio',
        root: targetRoot,
        fileCount: cold.project.fileCount,
      },
      method: 'Real QualityStudio.Api and Angular client; repository changes are made through the UI switcher.',
      baselineBefore: {
        source: 'QS-59 performance dossier, taskboard 5,116-file target',
        tree: { status: 200, backendMs: 659.10, bytesTransferred: 35_519_812 },
      },
      after: { cold, warm },
      assertions: {
        treeConditionalHeaderSent: Boolean(warm.tree.ifNoneMatch),
        dashboardConditionalHeaderSent: Boolean(warm.project.ifNoneMatch),
        warmTreeStatus304: warm.tree.status === 304,
        warmDashboardStatus304: warm.project.status === 304,
        warmTreeBytesZero: warm.tree.bytesTransferred === 0,
        warmTreeBackendWithin100Ms: warm.tree.backendRoundTripMs <= 100,
        transitionCleared: warm.transitionCleared,
      },
      apiPrewarmEvent: apiLines.find(line => line.includes('qs.repository.prewarm') && line.includes('agent-studio'))?.trim() ?? null,
      clientEvents: consoleEvents.filter(event =>
        event.repositoryId === 'agent-studio' && event.event === 'qs.repository.switch.usable'),
    };
    await writeFile(resolve(resultsRoot, 'tree-payload-perf.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));

    if (Object.values(result.assertions).some(value => value !== true)) process.exitCode = 1;
  } finally {
    await browser.close();
  }
} finally {
  await Promise.allSettled(children.map(stop));
  if (temporaryRoot) await rm(temporaryRoot, { recursive: true, force: true });
}

async function writeRegistry(apiHost) {
  const registryDirectory = resolve(apiHost, '.quality-studio');
  await mkdir(registryDirectory, { recursive: true });
  const entry = (id, displayName, rootPath) => ({
    id, displayName, rootPath, globalInputsDirectory: null, inputBudgetCharacters: 12_000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100_000, defaultReviewCostCap: null,
  });
  await writeFile(resolve(registryDirectory, 'repositories.json'), JSON.stringify([
    entry('default', 'Quality Studio', repositoryRoot),
    entry('agent-studio', 'Agent Studio', targetRoot),
  ], null, 2));
}

async function switchAndMeasure(page, displayName, repositoryId) {
  const escapedId = repositoryId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const treeResponse = page.waitForResponse(response =>
    new RegExp(`/api/repos/${escapedId}/tree(?:\\?|$)`).test(response.url()));
  const projectResponse = page.waitForResponse(response =>
    new RegExp(`/api/repos/${escapedId}/project(?:\\?|$)`).test(response.url()));
  const usableEventsBefore = await page.evaluate(() => performance.getEntriesByName('qs.repository.switch.usable').length);
  await page.locator('.repository-trigger').click();
  await page.getByRole('menuitemradio', { name: new RegExp(displayName, 'i') }).click();
  const [tree, project] = await Promise.all([treeResponse, projectResponse]);
  const [treeMeasurement, projectMeasurement] = await Promise.all([
    responseMeasurement(page, tree, false), responseMeasurement(page, project, true),
  ]);
  await page.waitForFunction(count => performance.getEntriesByName('qs.repository.switch.usable').length > count, usableEventsBefore);
  const transitionCleared = await page.locator('[data-transition-state]').count() === 0;
  return { tree: treeMeasurement, project: projectMeasurement, transitionCleared };
}

async function responseMeasurement(page, response, readFileCount) {
  const requestHeaders = await response.request().allHeaders();
  const responseHeaders = await response.allHeaders();
  const timing = response.request().timing();
  let body;
  if (response.status() !== 304) {
    try { body = await response.body(); } catch { /* Large payloads may be evicted; Resource Timing remains authoritative. */ }
  }
  await response.finished();
  const resource = await page.evaluate(url => {
    const entries = performance.getEntriesByName(url);
    const entry = entries.at(-1);
    return entry ? {
      duration: entry.duration,
      encodedBodySize: 'encodedBodySize' in entry ? entry.encodedBodySize : 0,
      decodedBodySize: 'decodedBodySize' in entry ? entry.decodedBodySize : 0,
    } : null;
  }, response.url());
  let fileCount = null;
  if (readFileCount && body?.length) {
    try { fileCount = JSON.parse(body.toString('utf8')).metrics?.fileCount ?? null; } catch { /* Evidence remains transport-valid. */ }
  }
  return {
    status: response.status(),
    backendRoundTripMs: +Math.max(0, resource?.duration ?? timing.responseEnd).toFixed(2),
    bytesTransferred: response.status() === 304 ? 0 : body?.length ?? resource?.decodedBodySize ?? 0,
    ifNoneMatch: requestHeaders['if-none-match'] ?? null,
    etag: responseHeaders.etag ?? null,
    fileCount,
  };
}

function start(name, command, arguments_, cwd, extraEnvironment, lines) {
  const child = spawn(command, arguments_, {
    cwd, env: { ...process.env, ...extraEnvironment }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  for (const stream of [child.stdout, child.stderr]) {
    stream.setEncoding('utf8');
    let buffer = '';
    stream.on('data', chunk => {
      buffer += chunk;
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        lines.push(line);
      }
    });
  }
  child.once('exit', code => {
    if (code && !child.killed) console.error(`${name} exited with code ${code}`);
  });
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

async function waitForHttp(url, timeoutMs) {
  await waitFor(async () => {
    try { return (await fetch(url)).ok; } catch { return false; }
  }, timeoutMs, url);
}

async function waitFor(predicate, timeoutMs, label) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (await predicate()) return;
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${label}`);
}
