import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { createReadStream, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, resolve, isAbsolute } from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import http from 'node:http';
import https from 'node:https';

const defaultRepoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
const defaultApiPort = Number(process.env.QUALITY_STUDIO_API_PORT ?? 5127);
const defaultWebPort = Number(process.env.QUALITY_STUDIO_PRODUCT_PORT ?? 4200);
const defaultHost = process.env.QUALITY_STUDIO_HOST ?? '127.0.0.1';
const defaultTimeoutMs = Number(process.env.QUALITY_STUDIO_START_TIMEOUT_MS ?? 120000);
const npmCommand = process.env.QUALITY_STUDIO_NPM_COMMAND ?? (process.platform === 'win32' ? 'npm.cmd' : 'npm');

const args = parseArgs(process.argv.slice(2));
const state = {
  shuttingDown: false,
  ready: false,
  children: [],
  tempPaths: [],
  exitCode: 0,
};

main().catch(async error => {
  console.error(formatLog('stack', `startup failed: ${error instanceof Error ? error.stack ?? error.message : String(error)}`));
  await shutdown(1);
});

async function main() {
  const repoRoot = resolvePath(args['repo-root'] ?? defaultRepoRoot);
  const frontendRoot = resolvePath(args['frontend-root'] ?? resolve(repoRoot, 'frontend'));
  const apiPort = Number(args['api-port'] ?? defaultApiPort);
  const webPort = Number(args['web-port'] ?? defaultWebPort);
  const host = args.host ?? defaultHost;
  const timeoutMs = Number(args['timeout-ms'] ?? defaultTimeoutMs);
  const apiBaseUrl = `http://${host}:${apiPort}`;
  const webBaseUrl = `http://${host}:${webPort}`;

  await ensureInstall(args, repoRoot, frontendRoot, apiPort, webPort);

  const proxyConfig = await createProxyConfig(apiPort);
  const api = await startChild('api', buildApiCommand(args, repoRoot, apiPort), {
    cwd: repoRoot,
    env: {
      QUALITY_STUDIO_API_PORT: String(apiPort),
      QUALITY_STUDIO_PRODUCT_PORT: String(webPort),
      QUALITY_STUDIO_HOST: host,
    },
  });
  const web = await startChild('web', buildWebCommand(args, webPort, host, proxyConfig), {
    cwd: frontendRoot,
    env: {
      QUALITY_STUDIO_API_PORT: String(apiPort),
      QUALITY_STUDIO_PRODUCT_PORT: String(webPort),
      QUALITY_STUDIO_HOST: host,
      CHOKIDAR_USEPOLLING: '1',
    },
  });

  const apiReady = waitForHttp(`${apiBaseUrl}/health`, timeoutMs, 'api');
  const webReady = waitForHttp(webBaseUrl, timeoutMs, 'web');

  await Promise.race([
    Promise.all([apiReady, webReady]),
    waitForChildExitBeforeReady(api),
    waitForChildExitBeforeReady(web),
  ]);

  state.ready = true;
  console.log(formatLog('stack', `ready: api=${apiBaseUrl} web=${webBaseUrl}`));

  await Promise.race([
    Promise.all([waitForChildExitAfterReady(api), waitForChildExitAfterReady(web)]),
    waitForSignal('SIGINT'),
    waitForSignal('SIGTERM'),
  ]);
}

function parseArgs(values) {
  const parsed = {};
  for (let index = 0; index < values.length; index++) {
    const value = values[index];
    if (!value.startsWith('--')) continue;
    const key = value.slice(2);
    const next = values[index + 1];
    if (next && !next.startsWith('--')) {
      parsed[key] = next;
      index++;
    } else {
      parsed[key] = 'true';
    }
  }
  return parsed;
}

function resolvePath(value) {
  return isAbsolute(value) ? value : resolve(process.cwd(), value);
}

async function ensureInstall(parsedArgs, repoRoot, frontendRoot, apiPort, webPort) {
  const installScript = parsedArgs['install-script'];
  if (installScript) {
    await runCommand('install', 'node', [installScript], {
      cwd: repoRoot,
      env: { QUALITY_STUDIO_API_PORT: String(apiPort), QUALITY_STUDIO_PRODUCT_PORT: String(webPort) },
    });
    return;
  }

  const installState = frontendInstallState(frontendRoot);
  if (installState.ready) return;

  console.log(formatLog('install', installState.reason));
  await runCommand('install', npmCommand, ['ci'], { cwd: frontendRoot });
}

function frontendInstallState(frontendRoot) {
  const nodeModules = resolve(frontendRoot, 'node_modules');
  const binDir = resolve(nodeModules, '.bin');
  const hasNodeModules = existsSync(nodeModules);
  const hasAngularCli = existsSync(resolve(binDir, 'ng')) || existsSync(resolve(binDir, 'ng.cmd'));
  if (!hasNodeModules) {
    return { ready: false, reason: 'frontend dependencies missing, running npm ci' };
  }
  if (!hasAngularCli) {
    return { ready: false, reason: 'frontend install incomplete, running npm ci' };
  }
  return { ready: true, reason: null };
}

function buildApiCommand(parsedArgs, repoRoot, apiPort) {
  if (parsedArgs['api-script']) {
    return ['node', parsedArgs['api-script']];
  }

  return [
    'dotnet',
    'run',
    '--project',
    resolve(repoRoot, 'src/QualityStudio.Api/QualityStudio.Api.csproj'),
    '--urls',
    `http://127.0.0.1:${apiPort}`,
    '--no-launch-profile',
  ];
}

function buildWebCommand(parsedArgs, webPort, host, proxyConfig) {
  if (parsedArgs['web-script']) {
    return ['node', parsedArgs['web-script']];
  }

  return [
    npmCommand,
    'start',
    '--',
    '--host',
    host,
    '--port',
    String(webPort),
    '--proxy-config',
    proxyConfig,
  ];
}

async function createProxyConfig(apiPort) {
  const tempDir = await mkdtemp(resolve(tmpdir(), 'quality-studio-dev-'));
  const proxyPath = resolve(tempDir, 'proxy.conf.json');
  await writeFile(proxyPath, JSON.stringify({
    '/api': { target: `http://127.0.0.1:${apiPort}`, secure: false, changeOrigin: true },
  }, null, 2));
  state.tempPaths.push(tempDir);
  return proxyPath;
}

async function startChild(name, command, options) {
  const child = spawn(command[0], command.slice(1), {
    cwd: options.cwd,
    env: { ...process.env, ...options.env },
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: process.platform === 'win32',
    detached: process.platform !== 'win32',
    windowsHide: true,
  });

  state.children.push({ name, process: child });
  pipeLogs(name, child.stdout, 'stdout');
  pipeLogs(name, child.stderr, 'stderr');
  child.once('exit', (code, signal) => {
    console.log(formatLog(name, `exited code=${code ?? 'null'} signal=${signal ?? 'null'}`));
  });
  return child;
}

function pipeLogs(name, stream, channel) {
  let buffer = '';
  stream.setEncoding('utf8');
  stream.on('data', chunk => {
    buffer += chunk;
    let index;
    while ((index = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, index).replace(/\r$/, '');
      buffer = buffer.slice(index + 1);
      if (line.length > 0) console.log(formatLog(name, `${channel}: ${line}`));
    }
  });
  stream.on('end', () => {
    if (buffer.trim().length > 0) console.log(formatLog(name, `${channel}: ${buffer.trimEnd()}`));
  });
}

async function runCommand(name, executable, args, options) {
  console.log(formatLog(name, `running ${executable} ${args.join(' ')}`));
  await new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(executable, args, {
      cwd: options.cwd,
      env: { ...process.env, ...options.env },
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: process.platform === 'win32',
      windowsHide: true,
    });

    pipeLogs(name, child.stdout, 'stdout');
    pipeLogs(name, child.stderr, 'stderr');
    child.once('error', rejectPromise);
    child.once('exit', code => code === 0 ? resolvePromise() : rejectPromise(new Error(`${name} command failed with exit code ${code}`)));
  });
}

async function waitForHttp(url, timeoutMs, name) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (state.shuttingDown) throw new Error(`${name} startup cancelled`);
    const result = await httpGet(url).catch(() => null);
    if (result?.statusCode && result.statusCode >= 200 && result.statusCode < 300) return result;
    await delay(500);
  }
  throw new Error(`${name} did not become ready within ${timeoutMs} ms`);
}

async function waitForChildExitBeforeReady(child) {
  return new Promise((resolvePromise, rejectPromise) => {
    child.once('exit', code => {
      if (!state.ready) rejectPromise(new Error(`process exited during startup with code ${code ?? 'null'}`));
      else resolvePromise();
    });
  });
}

async function waitForChildExitAfterReady(child) {
  return new Promise((resolvePromise, rejectPromise) => {
    child.once('exit', code => {
      if (!state.shuttingDown) {
        state.exitCode = code ?? 1;
        console.error(formatLog('stack', `${child.spawnargs[0]} exited unexpectedly with code ${code ?? 'null'}`));
        shutdown(1).then(() => rejectPromise(new Error('child exited unexpectedly'))).catch(rejectPromise);
        return;
      }
      resolvePromise();
    });
  });
}

function waitForSignal(signal) {
  return new Promise(resolvePromise => {
    process.once(signal, async () => {
      await shutdown(0);
      resolvePromise();
    });
  });
}

async function shutdown(exitCode) {
  if (state.shuttingDown) return;
  state.shuttingDown = true;
  state.exitCode = exitCode;
  await Promise.allSettled(state.children.map(entry => terminateProcess(entry.process)));
  await Promise.allSettled(state.tempPaths.map(path => rm(path, { recursive: true, force: true })));
  process.exitCode = exitCode;
}

async function terminateProcess(child) {
  if (child.killed || child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32') {
    await new Promise(resolvePromise => {
      const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
      killer.once('exit', () => resolvePromise());
      killer.once('error', () => resolvePromise());
    });
    return;
  }

  try {
    process.kill(-child.pid, 'SIGTERM');
  } catch {
    try { child.kill('SIGTERM'); } catch { /* ignore */ }
  }
  await delay(1000);
  try {
    process.kill(-child.pid, 'SIGKILL');
  } catch {
    try { child.kill('SIGKILL'); } catch { /* ignore */ }
  }
}

async function httpGet(url) {
  const parsed = new URL(url);
  const client = parsed.protocol === 'https:' ? https : http;
  return await new Promise((resolvePromise, rejectPromise) => {
    const request = client.request(parsed, response => {
      response.resume();
      response.once('end', () => resolvePromise({ statusCode: response.statusCode ?? 0 }));
    });
    request.once('error', rejectPromise);
    request.end();
  });
}

function formatLog(scope, message) {
  return `[${scope}] ${message}`;
}
