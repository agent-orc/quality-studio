import test from 'node:test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import {
  createNpmStub,
  createSandbox,
  expectMatch,
  fetchText,
  reserveFreePorts,
  runLauncher,
  terminate,
} from './helpers/dev-stack-harness.mjs';

test('launcher bootstraps a clean checkout, starts both services, and can restart cleanly', async () => {
  const sandbox = await createSandbox('qs-dev-stack-');
  const repoRoot = join(sandbox, 'repo');
  const frontendRoot = join(repoRoot, 'frontend');
  const marker = join(sandbox, 'install-count.txt');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  await mkdir(frontendRoot, { recursive: true });
  await writeFile(apiScript, serviceScript('api-ready'));
  await writeFile(webScript, serviceScript('web-ready'));
  const npmStub = await createNpmStub(sandbox);
  const [firstApiPort, firstWebPort, secondApiPort, secondWebPort] = await reserveFreePorts(4);

  const first = await runLauncher({
    args: launcherArgs({ repoRoot, frontendRoot, apiScript, webScript, apiPort: firstApiPort, webPort: firstWebPort }),
    env: { ...process.env, QUALITY_STUDIO_MARKER_FILE: marker, QUALITY_STUDIO_NPM_COMMAND: npmStub },
  });
  expectMatch(first, first.stdout, readyPattern(firstApiPort, firstWebPort), 'the first ready line');
  expectMatch(first, await readFile(marker, 'utf8'), /^ci\r?\n?$/, 'the install marker after the first run');

  await mkdir(join(frontendRoot, 'node_modules', '.bin'), { recursive: true });
  await writeFile(join(frontendRoot, 'node_modules', '.bin', 'ng'), '');

  const second = await runLauncher({
    args: launcherArgs({ repoRoot, frontendRoot, apiScript, webScript, apiPort: secondApiPort, webPort: secondWebPort }),
    env: { ...process.env, QUALITY_STUDIO_MARKER_FILE: marker, QUALITY_STUDIO_NPM_COMMAND: npmStub },
  });
  expectMatch(second, second.stdout, readyPattern(secondApiPort, secondWebPort), 'the second ready line');
  expectMatch(second, await readFile(marker, 'utf8'), /^ci\r?\n?$/, 'the install marker after the restart');
});

test('launcher reinstalls when node_modules is present but incomplete', async () => {
  const sandbox = await createSandbox('qs-dev-stack-partial-');
  const repoRoot = join(sandbox, 'repo');
  const frontendRoot = join(repoRoot, 'frontend');
  const marker = join(sandbox, 'install-count.txt');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  await mkdir(join(frontendRoot, 'node_modules'), { recursive: true });
  await writeFile(apiScript, serviceScript('api-ready'));
  await writeFile(webScript, serviceScript('web-ready'));
  const npmStub = await createNpmStub(sandbox);
  const [apiPort, webPort] = await reserveFreePorts(2);

  const result = await runLauncher({
    args: launcherArgs({ repoRoot, frontendRoot, apiScript, webScript, apiPort, webPort }),
    env: { ...process.env, QUALITY_STUDIO_MARKER_FILE: marker, QUALITY_STUDIO_NPM_COMMAND: npmStub },
  });

  expectMatch(result, result.stdout, /frontend install incomplete, running npm ci/, 'the reinstall reason');
  expectMatch(result, await readFile(marker, 'utf8'), /^ci\r?\n?$/, 'the install marker');
});

test('launcher fails if API never becomes ready', async () => {
  const sandbox = await createSandbox('qs-dev-stack-fail-');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const installScript = join(sandbox, 'install.mjs');
  await writeFile(apiScript, `process.exit(1);`);
  await writeFile(webScript, serviceScript('web-ready'));
  await writeFile(installScript, `process.exit(0);`);
  const [apiPort, webPort] = await reserveFreePorts(2);

  const result = await runLauncher({
    args: [...launcherArgs({ apiScript, webScript, apiPort, webPort }), '--install-script', installScript, '--timeout-ms', '3000'],
    expect: 'failure',
  });
  expectMatch(
    result,
    result.stderr,
    /exited unexpectedly|did not become ready|process exited during startup/,
    'the API startup failure',
  );
});

test('launcher fails if frontend exits before ready', async () => {
  const sandbox = await createSandbox('qs-dev-stack-webfail-');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const installScript = join(sandbox, 'install.mjs');
  await writeFile(apiScript, serviceScript('api-ready'));
  await writeFile(webScript, `process.exit(1);`);
  await writeFile(installScript, `process.exit(0);`);
  const [apiPort, webPort] = await reserveFreePorts(2);

  const result = await runLauncher({
    args: [...launcherArgs({ apiScript, webScript, apiPort, webPort }), '--install-script', installScript, '--timeout-ms', '3000'],
    expect: 'failure',
  });
  expectMatch(
    result,
    result.stderr,
    /exited unexpectedly|did not become ready|process exited during startup/,
    'the frontend startup failure',
  );
});

test('launcher reports a missing service executable instead of waiting for the readiness timeout', async () => {
  const sandbox = await createSandbox('qs-dev-stack-missing-');
  const repoRoot = join(sandbox, 'repo');
  const frontendRoot = join(repoRoot, 'frontend');
  const apiScript = join(sandbox, 'api.mjs');
  await mkdir(join(frontendRoot, 'node_modules', '.bin'), { recursive: true });
  await writeFile(join(frontendRoot, 'node_modules', '.bin', 'ng'), '');
  await writeFile(apiScript, serviceScript('api-ready'));
  const [apiPort, webPort] = await reserveFreePorts(2);
  const missingNpm = join(sandbox, 'npm-that-does-not-exist');

  // The readiness timeout is far larger than the test timeout, so reaching the
  // assertions at all proves the launcher failed fast instead of waiting it out.
  //
  // The two platforms reach that point differently and both are acceptable. On POSIX
  // the child is spawned directly, so a missing binary raises a spawn 'error' and the
  // launcher reports "failed to start". On Windows children go through cmd.exe, which
  // exists; the shell itself reports the missing command and exits non-zero, so the
  // launcher reports the early exit. What must hold on both is that the failure is
  // immediate and names the executable that could not be found.
  const result = await runLauncher({
    args: [
      '--repo-root', repoRoot,
      '--frontend-root', frontendRoot,
      '--api-script', apiScript,
      '--api-port', String(apiPort),
      '--web-port', String(webPort),
      '--timeout-ms', '600000',
    ],
    env: { ...process.env, QUALITY_STUDIO_NPM_COMMAND: missingNpm },
    expect: 'failure',
  });

  const transcript = result.stdout + result.stderr;
  expectMatch(result, transcript, /failed to start|exited during startup/, 'the startup failure diagnostic');
  expectMatch(result, transcript, /npm-that-does-not-exist/, 'the missing executable name');
  expectMatch(result, transcript, /startup failed/, 'the launcher startup summary');
});

test('embedded shell loads in an iframe and shows the live connection badge', async () => {
  const sandbox = await createSandbox('qs-dev-stack-frame-');
  const apiScript = join(sandbox, 'api.mjs');
  const webScript = join(sandbox, 'web.mjs');
  const installScript = join(sandbox, 'install.mjs');
  await writeFile(apiScript, liveApiScript());
  await writeFile(webScript, embeddedWebScript());
  await writeFile(installScript, `process.exit(0);`);
  const [apiPort, webPort] = await reserveFreePorts(2);

  const started = await runLauncher({
    args: [...launcherArgs({ apiScript, webScript, apiPort, webPort }), '--install-script', installScript, '--timeout-ms', '3000'],
    expect: 'ready-keep',
  });
  const dump = await fetchText(`http://127.0.0.1:${webPort}/embedded-test`);
  await terminate(started.child);
  expectMatch(started, dump, /<iframe/, 'the embedded document');
  expectMatch(started, dump, /Embedded/, 'the embedded badge');
  expectMatch(started, started.stdout, readyPattern(apiPort, webPort), 'the ready line');
});

function launcherArgs({ repoRoot, frontendRoot, apiScript, webScript, apiPort, webPort }) {
  const args = [];
  if (repoRoot) args.push('--repo-root', repoRoot);
  if (frontendRoot) args.push('--frontend-root', frontendRoot);
  if (apiScript) args.push('--api-script', apiScript);
  if (webScript) args.push('--web-script', webScript);
  args.push('--api-port', String(apiPort), '--web-port', String(webPort));
  return args;
}

function readyPattern(apiPort, webPort) {
  return new RegExp(`ready: api=http://127\\.0\\.0\\.1:${apiPort} web=http://127\\.0\\.0\\.1:${webPort}`);
}

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
