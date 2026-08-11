import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = resolve(frontendRoot, '..');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(tmpdir(), 'quality-studio-real-api-smoke');
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const [apiPort, webPort] = await freePorts(2);
const output = [];
let stack;
let browser;

try {
  await mkdir(resultsRoot, { recursive: true });
  stack = spawn(process.execPath, [resolve(repositoryRoot, 'scripts/dev-stack.mjs'),
    '--api-port', String(apiPort), '--web-port', String(webPort), '--timeout-ms', '120000'], {
    cwd: repositoryRoot,
    env: process.env,
    detached: process.platform !== 'win32',
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  capture(stack.stdout, 'stdout');
  capture(stack.stderr, 'stderr');
  await waitForReady(stack, 120000);

  browser = await chromium.launch({ executablePath, headless: true,
    args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.goto(`http://127.0.0.1:${webPort}/?theme=dark&path=.`, { waitUntil: 'domcontentloaded' });
  await page.locator('.health[data-connection-state="live"]').waitFor({ state: 'visible', timeout: 30000 });
  await page.locator('.project-dashboard .health-card').first().waitFor({ state: 'visible', timeout: 30000 });
  await page.getByRole('textbox', { name: 'Filter files' }).fill('Program.cs');
  await page.locator('.tree-row').first().click();
  await page.locator('.code-line').first().waitFor({ state: 'visible', timeout: 30000 });
  const result = {
    measuredAt: new Date().toISOString(),
    browser: await browser.version(),
    apiPort,
    webPort,
    connection: await page.locator('.health').getAttribute('data-connection-state'),
    openedPath: await page.locator('.breadcrumbs').textContent(),
  };
  await page.screenshot({ path: resolve(resultsRoot, 'real-api-smoke.png'), fullPage: true });
  await writeFile(resolve(resultsRoot, 'real-api-smoke.json'), `${JSON.stringify(result, null, 2)}\n`);
  console.log(JSON.stringify(result));
} catch (error) {
  throw new Error(`${error instanceof Error ? error.message : String(error)}\nCaptured stack output:\n${output.join('\n')}`);
} finally {
  if (browser) await browser.close();
  if (stack) await stop(stack);
}

function capture(stream, channel) {
  stream.setEncoding('utf8');
  stream.on('data', chunk => {
    for (const line of chunk.split(/\r?\n/).filter(Boolean)) {
      output.push(`${channel}: ${line}`);
      if (output.length > 100) output.shift();
    }
  });
}

async function waitForReady(child, timeoutMs) {
  await new Promise((resolvePromise, rejectPromise) => {
    const timeout = setTimeout(() => rejectPromise(new Error('Timed out waiting for the real dev stack.')), timeoutMs);
    const onData = chunk => {
      if (!chunk.includes('ready:')) return;
      clearTimeout(timeout);
      resolvePromise();
    };
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', onData);
    child.once('exit', code => {
      clearTimeout(timeout);
      rejectPromise(new Error(`The real dev stack exited before readiness with code ${code}.`));
    });
  });
}

async function freePorts(count) {
  const servers = [];
  const ports = [];
  try {
    for (let index = 0; index < count; index++) {
      const server = createServer();
      servers.push(server);
      await new Promise((resolvePromise, rejectPromise) => server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
      const address = server.address();
      ports.push(typeof address === 'object' && address ? address.port : 0);
    }
  } finally {
    await Promise.all(servers.map(server => new Promise(resolvePromise => server.close(resolvePromise))));
  }
  return ports;
}

async function stop(child) {
  if (child.exitCode !== null) return;
  if (process.platform === 'win32') child.kill('SIGINT');
  else {
    try { process.kill(-child.pid, 'SIGINT'); } catch { child.kill('SIGINT'); }
  }
  await Promise.race([
    new Promise(resolvePromise => child.once('exit', resolvePromise)),
    new Promise(resolvePromise => setTimeout(resolvePromise, 5000)),
  ]);
  if (child.exitCode === null) child.kill('SIGKILL');
}
