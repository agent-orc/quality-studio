import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { chromium } from 'playwright-core';

const testsRoot = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsRoot, '..');
const repositoryRoot = resolve(frontendRoot, '..');
const apiDll = process.env.QS_API_DLL || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Debug/net10.0/QualityStudio.Api.dll');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(frontendRoot, 'evidence');
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const transitionBudgetMs = 100;
const usableBudgetMs = 500;
const sampleSuffix = process.env.QS_SAMPLE_INDEX ? `-${process.env.QS_SAMPLE_INDEX}` : '';
const state = { children: [], tempRoot: null };

try {
  if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built. Expected ${apiDll}`);
  state.tempRoot = await mkdtemp(join(tmpdir(), 'qs-project-switch-perf-'));
  await mkdir(resultsRoot, { recursive: true });
  const smallRepository = resolve(state.tempRoot, 'small-repository');
  const realisticRepository = resolve(state.tempRoot, 'realistic-repository');
  const apiHost = resolve(state.tempRoot, 'api-host');
  await Promise.all([
    createFixtureRepository(smallRepository, 12),
    createFixtureRepository(realisticRepository, 1_600),
    mkdir(apiHost, { recursive: true }),
  ]);
  await writeRegistry(apiHost, smallRepository, realisticRepository);

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
  await waitFor(() => apiLines.some(line => line.includes('"event":"qs.repository.prewarm"') && line.includes('"repositoryId":"realistic"')), 60_000,
    'realistic repository prewarm event');

  const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const events = [];
    page.on('console', message => {
      try {
        const event = JSON.parse(message.text());
        if (event.event?.startsWith('qs.')) events.push(event);
      } catch { /* Angular and browser diagnostics are not structured product events. */ }
    });

    await page.goto(`http://127.0.0.1:${webPort}/?theme=light&repo=default&path=.`);
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });

    await switchRepository(page, 'Realistic fixture');
    await page.locator('[data-transition-state]').first().waitFor({ state: 'visible' });
    await page.screenshot({ path: resolve(resultsRoot, `project-switch-transition-light${sampleSuffix}.png`), fullPage: true });
    await page.waitForFunction(() => performance.getEntriesByName('qs.repository.switch.usable').length >= 1);
    await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible' });

    await switchRepository(page, 'Small fixture');
    await page.waitForFunction(() => performance.getEntriesByName('qs.repository.switch.usable').length >= 2);
    await page.getByRole('button', { name: 'Switch to dark theme' }).click();
    await switchRepository(page, 'Realistic fixture');
    await page.locator('[data-transition-state="stale"]').waitFor({ state: 'visible' });
    await page.screenshot({ path: resolve(resultsRoot, `project-switch-transition-dark${sampleSuffix}.png`), fullPage: true });
    await page.waitForFunction(() => performance.getEntriesByName('qs.repository.switch.usable').length >= 3);

    const transitionEvents = events.filter(event => event.event === 'qs.repository.transition-visible');
    const usableEvents = events.filter(event => event.event === 'qs.repository.switch.usable');
    const projectEvents = events.filter(event => event.event === 'qs.project.first-interactive');
    const result = {
      measuredAt: new Date().toISOString(),
      browser: await browser.version(),
      fixtureFiles: 1_600,
      backend: 'real QualityStudio.Api (no Playwright response interception)',
      budgets: { transitionVisibleMs: transitionBudgetMs, usableMs: usableBudgetMs },
      transitionEvents,
      usableEvents,
      projectEvents,
      prewarmEvent: apiLines.find(line => line.includes('"event":"qs.repository.prewarm"') && line.includes('"repositoryId":"realistic"'))?.trim() ?? null,
    };
    await writeFile(resolve(resultsRoot, `project-switch-perf${sampleSuffix}.json`), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));

    const realisticProjectEvents = projectEvents.filter(event => event.repositoryId === 'realistic');
    if (transitionEvents.length < 3 || transitionEvents.some(event => event.durationMs >= transitionBudgetMs) ||
        usableEvents.length < 3 || usableEvents.some(event => event.durationMs >= usableBudgetMs) ||
        realisticProjectEvents.length < 2 || realisticProjectEvents.some(event => event.durationMs >= 150)) process.exitCode = 1;
  } finally {
    await browser.close();
  }
} finally {
  await Promise.allSettled(state.children.map(stop));
  if (state.tempRoot) await rm(state.tempRoot, { recursive: true, force: true });
}

async function createFixtureRepository(root, fileCount) {
  await mkdir(resolve(root, 'src'), { recursive: true });
  await writeFile(resolve(root, 'angular.json'), JSON.stringify({ projects: { fixture: { root: '', sourceRoot: 'src' } } }));
  await writeFile(resolve(root, 'package.json'), JSON.stringify({ name: `quality-studio-switch-${fileCount}`, private: true }));
  await writeFile(resolve(root, '.gitignore'), 'node_modules\ndist\ncoverage\n');
  for (let start = 0; start < fileCount; start += 100) {
    await Promise.all(Array.from({ length: Math.min(100, fileCount - start) }, async (_, offset) => {
      const index = start + offset;
      const feature = `feature-${String(index % 40).padStart(2, '0')}`;
      const directory = resolve(root, 'src', feature);
      await mkdir(directory, { recursive: true });
      const lines = [
        `export interface Record${index} {`,
        '  id: string;',
        '  updatedAt: string;',
        '}',
        '',
        `export class RepositoryService${index} {`,
        `  readonly feature = '${feature}';`,
        `  select(records: Record${index}[], id: string): Record${index} | undefined {`,
        '    return records.find(record => record.id === id);',
        '  }',
        '}',
        '',
      ];
      if (index % 7 === 0) lines.push(`export const fixture${index} = ${JSON.stringify('x'.repeat(240 + index % 400))};`, '');
      await writeFile(resolve(directory, `repository-service-${String(index).padStart(4, '0')}.ts`), lines.join('\n'));
    }));
  }
  runGit(root, 'init', '--quiet');
  runGit(root, 'config', 'user.email', 'perf-harness@example.invalid');
  runGit(root, 'config', 'user.name', 'Quality Studio Perf Harness');
  runGit(root, 'add', '.');
  runGit(root, 'commit', '--quiet', '-m', 'Create realistic project-switch fixture');
}

async function writeRegistry(apiHost, smallRepository, realisticRepository) {
  const registryDirectory = resolve(apiHost, '.quality-studio');
  await mkdir(registryDirectory, { recursive: true });
  const entry = (id, displayName, rootPath) => ({
    id, displayName, rootPath, globalInputsDirectory: null, inputBudgetCharacters: 12000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
  });
  await writeFile(resolve(registryDirectory, 'repositories.json'), JSON.stringify([
    entry('default', 'Small fixture', smallRepository),
    entry('realistic', 'Realistic fixture', realisticRepository),
  ], null, 2));
}

function runGit(root, ...arguments_) {
  const result = spawnSync('git', ['-C', root, ...arguments_], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`git ${arguments_.join(' ')} failed: ${result.stderr}`);
}

function start(name, command, arguments_, cwd, extraEnvironment, lines) {
  const child = spawn(command, arguments_, { cwd, env: { ...process.env, ...extraEnvironment }, stdio: ['ignore', 'pipe', 'pipe'] });
  state.children.push(child);
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
