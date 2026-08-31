import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { chromium } from '../../frontend/node_modules/playwright-core/index.mjs';

const scriptRoot = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptRoot, '../..');
const frontendRoot = resolve(repositoryRoot, 'frontend');
const apiDll = process.env.QS_API_DLL
  || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Debug/net10.0/QualityStudio.Api.dll');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const fileCount = 5_116;
const state = { children: [], tempRoot: null };

try {
  if (!existsSync(apiDll)) throw new Error(`Build the API first; expected ${apiDll}`);
  if (!existsSync(executablePath)) throw new Error(`Chromium is unavailable; expected ${executablePath}`);
  state.tempRoot = await mkdtemp(join(tmpdir(), 'qs-tree-payload-perf-'));
  await mkdir(resultsRoot, { recursive: true });

  const smallRepository = resolve(state.tempRoot, 'small');
  const taskboardRepository = resolve(state.tempRoot, 'agent-studio-taskboard');
  const apiHost = resolve(state.tempRoot, 'api-host');
  await Promise.all([
    createRepository(smallRepository, 3),
    createRepository(taskboardRepository, fileCount),
    mkdir(apiHost, { recursive: true }),
  ]);
  await writeRegistry(apiHost, smallRepository, taskboardRepository);

  const apiPort = await freePort();
  const webPort = await freePort();
  const proxyPath = resolve(state.tempRoot, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: true },
  }));

  const apiLines = [];
  start('api', 'dotnet', [apiDll, '--urls', `http://127.0.0.1:${apiPort}`, '--contentRoot', apiHost], apiHost, {
    QualityStudio__RepositoryRoot: smallRepository,
    QualityStudio__AllowedRoots__0: state.tempRoot,
    QualityStudio__Security__Mode: 'Local',
  }, apiLines);
  await waitForHttp(`http://127.0.0.1:${apiPort}/health`, 30_000);

  const ngCli = resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js');
  start('web', process.execPath, [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(webPort), '--proxy-config', proxyPath], frontendRoot, {}, []);
  await waitForHttp(`http://127.0.0.1:${webPort}`, 60_000);
  await waitFor(() => apiLines.some(line => line.includes('qs.repository.prewarm') && line.includes('taskboard')), 120_000,
    'taskboard prewarm');

  const browser = await chromium.launch({ executablePath, headless: true, args: ['--no-sandbox'] });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const treeRequests = [];
    page.on('response', response => {
      if (!response.url().includes('/api/repos/taskboard/tree?path=')) return;
      treeRequests.push(captureTreeResponse(response));
    });

    await page.goto(`http://127.0.0.1:${webPort}/?theme=light&repo=default&path=.`);
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });

    await switchRepository(page, 'Agent Studio (taskboard)');
    await switchRepository(page, 'Small fixture');
    await switchRepository(page, 'Agent Studio (taskboard)');

    await writeFile(resolve(taskboardRepository, 'src', 'head-change.ts'), 'export const headChange = true;\n');
    runGit(taskboardRepository, 'add', '.');
    runGit(taskboardRepository, 'commit', '--quiet', '-m', 'Invalidate hierarchy ETag');
    await switchRepository(page, 'Small fixture');
    await switchRepository(page, 'Agent Studio (taskboard)');

    const captured = await Promise.all(treeRequests);
    const [before, after, invalidated] = captured;
    const result = {
      schemaVersion: 1,
      measuredAt: new Date().toISOString(),
      target: { repositoryId: 'taskboard', label: 'Agent Studio (taskboard)', files: fileCount },
      measurement: 'Real Angular client repository switches against the real QualityStudio.Api on loopback; backendMs is browser-observed request-to-response time and bytes is HTTP response-body bytes.',
      warmSwitch: { before, after },
      invalidation: invalidated,
      suppliedDossierBaseline: { backendMs: 659.10, bytes: 35_519_812 },
      browser: await browser.version(),
    };
    await writeFile(resolve(resultsRoot, 'tree-payload-perf.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));

    if (captured.length !== 3
      || before.status !== 200 || before.ifNoneMatch !== null
      || after.status !== 304 || after.ifNoneMatch !== before.etag || after.bytes !== 0 || after.backendMs > 100
      || invalidated.status !== 200 || invalidated.ifNoneMatch !== before.etag || invalidated.etag === before.etag) {
      process.exitCode = 1;
    }
  } finally {
    await browser.close();
  }
} finally {
  await Promise.allSettled(state.children.map(stop));
  if (state.tempRoot) await rm(state.tempRoot, { recursive: true, force: true });
}

async function captureTreeResponse(response) {
  await response.finished();
  const timing = response.request().timing();
  return {
    status: response.status(),
    backendMs: Math.round(timing.responseEnd * 100) / 100,
    bytes: response.status() === 304 ? 0 : (await response.body()).byteLength,
    ifNoneMatch: (await response.request().allHeaders())['if-none-match'] ?? null,
    etag: (await response.allHeaders()).etag ?? null,
  };
}

async function createRepository(root, count) {
  await mkdir(resolve(root, 'src'), { recursive: true });
  await writeFile(resolve(root, 'angular.json'), JSON.stringify({ projects: { taskboard: { root: '', sourceRoot: 'src' } } }));
  await writeFile(resolve(root, 'package.json'), JSON.stringify({ name: `taskboard-${count}`, private: true }));
  await writeFile(resolve(root, '.gitignore'), 'node_modules\ndist\ncoverage\n');
  for (let startIndex = 0; startIndex < count; startIndex += 200) {
    await Promise.all(Array.from({ length: Math.min(200, count - startIndex) }, async (_, offset) => {
      const index = startIndex + offset;
      const feature = `feature-${String(index % 48).padStart(2, '0')}`;
      const directory = resolve(root, 'src', feature);
      await mkdir(directory, { recursive: true });
      await writeFile(resolve(directory, `task-${String(index).padStart(4, '0')}.ts`),
        `export interface Task${index} { id: string; state: 'queued' | 'running' | 'done'; }\n`
        + `export const task${index}: Task${index} = { id: '${index}', state: 'queued' };\n`);
    }));
  }
  runGit(root, 'init', '--quiet');
  runGit(root, 'config', 'user.email', 'perf-harness@example.invalid');
  runGit(root, 'config', 'user.name', 'Quality Studio Perf Harness');
  runGit(root, 'add', '.');
  runGit(root, 'commit', '--quiet', '-m', 'Create project-switch fixture');
}

async function writeRegistry(apiHost, smallRepository, taskboardRepository) {
  const registryDirectory = resolve(apiHost, '.quality-studio');
  await mkdir(registryDirectory, { recursive: true });
  const entry = (id, displayName, rootPath) => ({
    id, displayName, rootPath, globalInputsDirectory: null, inputBudgetCharacters: 12_000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100_000, defaultReviewCostCap: null,
  });
  await writeFile(resolve(registryDirectory, 'repositories.json'), JSON.stringify([
    entry('default', 'Small fixture', smallRepository),
    entry('taskboard', 'Agent Studio (taskboard)', taskboardRepository),
  ], null, 2));
}

function runGit(root, ...arguments_) {
  const result = spawnSync('git', ['-C', root, ...arguments_], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`git ${arguments_.join(' ')} failed: ${result.stderr}`);
}

function start(name, command, arguments_, cwd, extraEnvironment, lines) {
  const child = spawn(command, arguments_, {
    cwd, env: { ...process.env, ...extraEnvironment }, stdio: ['ignore', 'pipe', 'pipe'],
  });
  state.children.push(child);
  for (const stream of [child.stdout, child.stderr]) {
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

async function switchRepository(page, name) {
  const repositoryId = name.includes('taskboard') ? 'taskboard' : 'default';
  const treeResponse = page.waitForResponse(response =>
    response.url().includes(`/api/repos/${repositoryId}/tree?path=`));
  await page.locator('.repository-trigger').click();
  await page.getByRole('menuitemradio', { name }).click();
  await treeResponse;
  await page.waitForFunction(() => !document.querySelector('[data-transition-state]'));
}
