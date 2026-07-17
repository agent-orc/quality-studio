import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import http from 'node:http';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const launcher = resolve(repoRoot, 'scripts', 'dev-stack.mjs');

test('launcher bootstraps a clean checkout, starts both services, and can restart cleanly', async () => {
  const sandbox = await mkdtemp(join(tmpdir(), 'qs-dev-stack-'));
  const repoRoot = join(sandbox, 'repo');
  const frontendRoot = join(repoRoot, 'frontend');
  const marker = join(sandbox, 'install-count.txt');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const npmStub = join(sandbox, 'npm.cmd');
  await mkdir(frontendRoot, { recursive: true });
  await writeFile(apiScript, serviceScript('api-ready'));
  await writeFile(webScript, serviceScript('web-ready'));
  await writeFile(npmStub, `@echo off\r\necho ci>>"%QUALITY_STUDIO_MARKER_FILE%"\r\nexit /b 0\r\n`);

  const first = await runLauncher({
    args: ['--repo-root', repoRoot, '--frontend-root', frontendRoot, '--api-script', apiScript, '--web-script', webScript, '--api-port', '51271', '--web-port', '42071'],
    env: { ...process.env, QUALITY_STUDIO_MARKER_FILE: marker, QUALITY_STUDIO_NPM_COMMAND: npmStub },
  });
  assert.match(first.stdout, /ready: api=http:\/\/127\.0\.0\.1:51271 web=http:\/\/127\.0\.0\.1:42071/);
  assert.match(await readFile(marker, 'utf8'), /^ci\r?\n?$/);

  await mkdir(join(frontendRoot, 'node_modules', '.bin'), { recursive: true });
  await writeFile(join(frontendRoot, 'node_modules', '.bin', 'ng'), '');

  const second = await runLauncher({
    args: ['--repo-root', repoRoot, '--frontend-root', frontendRoot, '--api-script', apiScript, '--web-script', webScript, '--api-port', '51272', '--web-port', '42072'],
    env: { ...process.env, QUALITY_STUDIO_MARKER_FILE: marker, QUALITY_STUDIO_NPM_COMMAND: npmStub },
  });
  assert.match(second.stdout, /ready: api=http:\/\/127\.0\.0\.1:51272 web=http:\/\/127\.0\.0\.1:42072/);
  assert.match(await readFile(marker, 'utf8'), /^ci\r?\n?$/);
});

test('launcher reinstalls when node_modules is present but incomplete', async () => {
  const sandbox = await mkdtemp(join(tmpdir(), 'qs-dev-stack-partial-'));
  const repoRoot = join(sandbox, 'repo');
  const frontendRoot = join(repoRoot, 'frontend');
  const marker = join(sandbox, 'install-count.txt');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const npmStub = join(sandbox, 'npm.cmd');
  await mkdir(frontendRoot, { recursive: true });
  await mkdir(join(frontendRoot, 'node_modules'), { recursive: true });
  await writeFile(apiScript, serviceScript('api-ready'));
  await writeFile(webScript, serviceScript('web-ready'));
  await writeFile(npmStub, `@echo off\r\necho ci>>"%QUALITY_STUDIO_MARKER_FILE%"\r\nexit /b 0\r\n`);

  const result = await runLauncher({
    args: ['--repo-root', repoRoot, '--frontend-root', frontendRoot, '--api-script', apiScript, '--web-script', webScript, '--api-port', '51276', '--web-port', '42076'],
    env: { ...process.env, QUALITY_STUDIO_MARKER_FILE: marker, QUALITY_STUDIO_NPM_COMMAND: npmStub },
  });

  assert.match(result.stdout, /frontend install incomplete, running npm ci/);
  assert.match(await readFile(marker, 'utf8'), /^ci\r?\n?$/);
});

test('launcher fails if API never becomes ready', async () => {
  const sandbox = await mkdtemp(join(tmpdir(), 'qs-dev-stack-fail-'));
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const installScript = join(sandbox, 'install.mjs');
  await writeFile(apiScript, `process.exit(1);`);
  await writeFile(webScript, serviceScript('web-ready'));
  await writeFile(installScript, `process.exit(0);`);

  const result = await runLauncher({
    args: ['--api-script', apiScript, '--web-script', webScript, '--install-script', installScript, '--api-port', '51273', '--web-port', '42073', '--timeout-ms', '3000'],
    expectFailure: true,
  });
  assert.match(result.stderr, /exited unexpectedly|did not become ready|process exited during startup/);
});

test('launcher fails if frontend exits before ready', async () => {
  const sandbox = await mkdtemp(join(tmpdir(), 'qs-dev-stack-webfail-'));
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const installScript = join(sandbox, 'install.mjs');
  await writeFile(apiScript, serviceScript('api-ready'));
  await writeFile(webScript, `process.exit(1);`);
  await writeFile(installScript, `process.exit(0);`);

  const result = await runLauncher({
    args: ['--api-script', apiScript, '--web-script', webScript, '--install-script', installScript, '--api-port', '51274', '--web-port', '42074', '--timeout-ms', '3000'],
    expectFailure: true,
  });
  assert.match(result.stderr, /exited unexpectedly|did not become ready|process exited during startup/);
});

test('embedded shell loads in an iframe and shows the live connection badge', async () => {
  const sandbox = await mkdtemp(join(tmpdir(), 'qs-dev-stack-frame-'));
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const installScript = join(sandbox, 'install.mjs');
  await writeFile(apiScript, liveApiScript());
  await writeFile(webScript, embeddedWebScript());
  await writeFile(installScript, `process.exit(0);`);

  const started = await runLauncher({
    args: ['--api-script', apiScript, '--web-script', webScript, '--install-script', installScript, '--api-port', '51275', '--web-port', '42075', '--timeout-ms', '3000'],
    expectReadyOnly: true,
  });
  const dump = await fetchText('http://127.0.0.1:42075/embedded-test');
  await terminate(started.child);
  assert.match(dump, /<iframe/);
  assert.match(dump, /Embedded/);
  assert.match(started.stdout, /ready: api=http:\/\/127\.0\.0\.1:51275 web=http:\/\/127\.0\.0\.1:42075/);
});

function serviceScript(label) {
  return `
import http from 'node:http';
const port = Number(process.env.QUALITY_STUDIO_${label.startsWith('api') ? 'API' : 'PRODUCT'}_PORT);
const server = http.createServer((request, response) => {
  if (${JSON.stringify(label)} === 'api-ready' && request.url === '/health') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ status: 'ok', service: 'mock-api' }));
    return;
  }
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  response.end('<html><body><main><div class="health">${label === 'api-ready' ? 'Repository connected' : 'Preview data'}</div></main></body></html>');
});
server.listen(port, '127.0.0.1', () => console.log('${label} listening on ' + port));
setTimeout(() => {}, 30000);
`;
}

function liveApiScript() {
  return `
import http from 'node:http';
const port = Number(process.env.QUALITY_STUDIO_API_PORT);
const json = body => JSON.stringify(body);
const server = http.createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  let statusCode = 404;
  let body = json({ status: 'missing' });
  if (url.pathname === '/health') {
    statusCode = 200;
    body = json({ status: 'ok', service: 'mock-api' });
  } else if (url.pathname === '/api/tree') {
    statusCode = 200;
    body = json({ nodes: [{ id: 'root', name: 'Quality Studio', level: 'repository', path: '.', kinds: { code: { direct: 'fresh', descendants: 'fresh', overall: 'fresh', score: 91, band: 'A', metaPath: 'review-meta.json' } }, children: [] }] });
  } else if (url.pathname === '/api/scan') {
    statusCode = 200;
    body = json({ files: [], freshCount: 1, staleCount: 0, missingCount: 0 });
  } else if (url.pathname === '/api/inputs') {
    statusCode = 200;
    body = json({ level: 'file', kinds: { code: { kind: 'code', level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] }, security: { kind: 'security', level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] }, performance: { kind: 'performance', level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] } } });
  } else if (url.pathname === '/api/file') {
    statusCode = 200;
    body = json({ path: url.searchParams.get('path') ?? 'src/QualityStudio.Api/Program.cs', content: 'console.log("hello");', metaDocuments: [{ reviewedAt: '2026-07-11T16:20:00.000Z', kind: 'code', reviewer: { agent: 'quality-reviewer', model: 'gpt-5' }, grade: { score: 91, band: 'A', rationale: 'Live data.' }, summary: 'Live file.', findings: [] }] });
  } else if (url.pathname === '/api/handover') {
    statusCode = 200;
    body = json({ targetConfigured: false, dryRun: true });
  }
  response.writeHead(statusCode, { 'content-type': 'application/json; charset=utf-8' });
  response.end(body);
});
server.listen(port, '127.0.0.1', () => console.log('live api listening on ' + port));
setTimeout(() => {}, 30000);
`;
}

function embeddedWebScript() {
  return `
import http from 'node:http';
const port = Number(process.env.QUALITY_STUDIO_PRODUCT_PORT);
const shell = '<!doctype html><html><body><main><div class="health" data-connection-state="live"><span class="status fresh"></span><span>Repository connected</span><span class="embedded-badge">Embedded</span></div></main></body></html>';
const embedded = '<!doctype html><html><body><div id="state">pending</div><iframe id="shell" src="/"></iframe><script>const frame = document.getElementById("shell"); frame.addEventListener("load", () => { const badge = frame.contentDocument.querySelector(".embedded-badge")?.textContent ?? "missing"; const health = frame.contentDocument.querySelector(".health")?.textContent ?? ""; document.getElementById("state").textContent = badge === "Embedded" && health.includes("Repository connected") ? "embedded-ok" : "embedded-bad"; });</script></body></html>';
const server = http.createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  const body = url.pathname === '/embedded-test' ? embedded : shell;
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  response.end(body);
});
server.listen(port, '127.0.0.1', () => console.log('embedded web listening on ' + port));
setTimeout(() => {}, 30000);
`;
}

async function runLauncher({ args, env = process.env, expectFailure = false, expectReadyOnly = false }) {
  const child = spawn(process.execPath, [launcher, ...args], {
    cwd: repoRoot,
    env,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let stdout = '';
  let stderr = '';
  child.stdout.on('data', chunk => stdout += chunk.toString('utf8'));
  child.stderr.on('data', chunk => stderr += chunk.toString('utf8'));

  if (expectFailure) {
    const exitCode = await new Promise(resolvePromise => child.once('exit', code => resolvePromise(code ?? 0)));
    assert.notEqual(exitCode, 0);
    return { stdout, stderr, child };
  }

  if (expectReadyOnly) {
    await waitForReady(child);
    return { stdout, stderr, child };
  }

  await waitForReady(child);
  await terminate(child);
  return { stdout, stderr, child };
}

async function waitForReady(child) {
  await new Promise((resolvePromise, rejectPromise) => {
    const timeout = setTimeout(() => rejectPromise(new Error('launcher did not become ready')), 15000);
    child.stdout.on('data', chunk => {
      if (chunk.toString('utf8').includes('ready:')) {
        clearTimeout(timeout);
        resolvePromise();
      }
    });
    child.once('exit', code => {
      clearTimeout(timeout);
      rejectPromise(new Error(`launcher exited early with ${code}`));
    });
  });
}

async function terminate(child) {
  child.kill('SIGINT');
  await new Promise(resolve => child.once('exit', resolve));
}

async function fetchText(url) {
  const response = await fetch(url);
  assert.equal(response.status, 200);
  return await response.text();
}
