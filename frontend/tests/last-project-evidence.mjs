import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { chromium } from 'playwright-core';

const testsRoot = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsRoot, '..');
const repositoryRoot = resolve(frontendRoot, '..');
const apiDll = process.env.QS_API_DLL || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Debug/net10.0/QualityStudio.Api.dll');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(frontendRoot, 'evidence');
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const state = { children: [], tempRoot: null };

try {
  if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built. Expected ${apiDll}`);
  state.tempRoot = await mkdtemp(resolve(tmpdir(), 'qs-last-project-evidence-'));
  await mkdir(resultsRoot, { recursive: true });
  const repositoryA = resolve(state.tempRoot, 'repository-a');
  const repositoryB = resolve(state.tempRoot, 'repository-b');
  const apiHost = resolve(state.tempRoot, 'api-host');
  await Promise.all([
    createFixtureRepository(repositoryA),
    createFixtureRepository(repositoryB),
    mkdir(apiHost, { recursive: true }),
  ]);
  await writeRegistry(apiHost, repositoryA, repositoryB);

  const apiPort = await freePort();
  const webPort = await freePort();
  const proxyPath = resolve(state.tempRoot, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: true },
  }));

  start('api', 'dotnet', [apiDll, '--urls', `http://127.0.0.1:${apiPort}`, '--contentRoot', apiHost], apiHost, {
    QualityStudio__RepositoryRoot: repositoryA,
    QualityStudio__AllowedRoots__0: state.tempRoot,
    QualityStudio__Security__Mode: 'Local',
  });
  await waitForHttp(`http://127.0.0.1:${apiPort}/health`, 30_000);

  const ngCli = resolve(frontendRoot, 'node_modules/@angular/cli/bin/ng.js');
  start('web', process.execPath, [ngCli, 'serve', '--host', '127.0.0.1', '--port', String(webPort), '--proxy-config', proxyPath], frontendRoot, {});
  await waitForHttp(`http://127.0.0.1:${webPort}`, 60_000);

  const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });

    // First visit: no ?repo= param, no prior localStorage — lands on the server default (Repository A).
    await page.goto(`http://127.0.0.1:${webPort}/?theme=light`);
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });
    const initialSelection = await page.locator('.repository-trigger').innerText();
    if (!/Repository A/.test(initialSelection)) throw new Error(`Expected the default repository on first visit, got "${initialSelection}"`);

    // Switch to Repository B, exactly as an operator would from the SWITCH REPOSITORY dropdown.
    await switchRepository(page, 'Repository B');
    await page.locator('.repository-trigger', { hasText: 'Repository B' }).waitFor({ state: 'visible' });
    await page.screenshot({ path: resolve(resultsRoot, 'last-project-before-reload.png'), fullPage: true });

    // Simulate an app restart: reload with no ?repo= param, i.e. no manual re-selection available.
    await page.reload();
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });
    const restoredSelection = await page.locator('.repository-trigger').innerText();
    await page.screenshot({ path: resolve(resultsRoot, 'last-project-after-reload.png'), fullPage: true });

    const result = {
      measuredAt: new Date().toISOString(),
      browser: await browser.version(),
      scenario: 'restart without ?repo= query param restores the last manually selected repository',
      initialSelection: initialSelection.trim(),
      selectedBeforeReload: 'Repository B',
      restoredSelectionAfterReload: restoredSelection.trim(),
      pass: /Repository B/.test(restoredSelection),
    };
    await writeFile(resolve(resultsRoot, 'last-project-evidence.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));

    if (!result.pass) throw new Error(`Expected Repository B to be restored after reload, got "${restoredSelection}"`);
  } finally {
    await browser.close();
  }
} finally {
  await Promise.allSettled(state.children.map(stop));
  if (state.tempRoot) await rm(state.tempRoot, { recursive: true, force: true });
}

async function createFixtureRepository(root) {
  await mkdir(resolve(root, 'src'), { recursive: true });
  await writeFile(resolve(root, 'angular.json'), JSON.stringify({ projects: { fixture: { root: '', sourceRoot: 'src' } } }));
  await writeFile(resolve(root, 'package.json'), JSON.stringify({ name: 'quality-studio-last-project-fixture', private: true }));
  await writeFile(resolve(root, '.gitignore'), 'node_modules\ndist\ncoverage\n');
  await writeFile(resolve(root, 'src', 'main.ts'), 'export const value = 1;\n');
  await runGit(root, 'init', '--quiet');
  await runGit(root, 'config', 'user.email', 'evidence-harness@example.invalid');
  await runGit(root, 'config', 'user.name', 'Quality Studio Evidence Harness');
  await runGit(root, 'add', '.');
  await runGit(root, 'commit', '--quiet', '-m', 'Create last-project fixture');
}

async function writeRegistry(apiHost, repositoryA, repositoryB) {
  const registryDirectory = resolve(apiHost, '.quality-studio');
  await mkdir(registryDirectory, { recursive: true });
  const entry = (id, displayName, rootPath) => ({
    id, displayName, rootPath, globalInputsDirectory: null, inputBudgetCharacters: 12000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
  });
  await writeFile(resolve(registryDirectory, 'repositories.json'), JSON.stringify([
    entry('default', 'Repository A', repositoryA),
    entry('repository-b', 'Repository B', repositoryB),
  ], null, 2));
}

function runGit(root, ...arguments_) {
  const result = spawn('git', ['-C', root, ...arguments_], { stdio: 'ignore' });
  return new Promise((resolvePromise, rejectPromise) => {
    result.once('exit', code => code === 0 ? resolvePromise() : rejectPromise(new Error(`git ${arguments_.join(' ')} failed`)));
  });
}

function start(name, command, arguments_, cwd, extraEnvironment) {
  const child = spawn(command, arguments_, { cwd, env: { ...process.env, ...extraEnvironment }, stdio: 'ignore' });
  state.children.push(child);
  child.once('exit', code => {
    if (code && !child.killed) console.error(`${name} exited with code ${code}`);
  });
  return child;
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
  await page.locator('.repository-trigger').click();
  await page.getByRole('menuitemradio', { name: new RegExp(name, 'i') }).click();
}
