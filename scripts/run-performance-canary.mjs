import { spawn, spawnSync } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { arch, cpus, platform, release, totalmem } from 'node:os';
import { resolve } from 'node:path';

const repositoryRoot = resolve(import.meta.dirname, '..');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'artifacts/canary/browser');
const [apiPort, webPort] = await freePorts(2);
const stackOutput = [];
let stack;

try {
  await mkdir(resultsRoot, { recursive: true });
  await writeFile(resolve(resultsRoot, 'host-metadata.json'), `${JSON.stringify({
    measuredAt: new Date().toISOString(),
    os: { platform: platform(), release: release(), arch: arch(), totalMemoryBytes: totalmem() },
    cpu: cpus()[0]?.model ?? 'unknown',
    logicalCpuCount: cpus().length,
    dotnet: version('dotnet', ['--version']),
    node: process.version,
    runner: { name: process.env.RUNNER_NAME ?? null, labels: process.env.RUNNER_LABELS ?? null },
  }, null, 2)}\n`);

  stack = spawn(process.execPath, [resolve(repositoryRoot, 'scripts/dev-stack.mjs'),
    '--api-port', String(apiPort), '--web-port', String(webPort), '--timeout-ms', '120000'], {
    cwd: repositoryRoot,
    env: process.env,
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  capture(stack.stdout, 'stdout');
  capture(stack.stderr, 'stderr');
  await waitForReady(stack, 120000);

  for (let sample = 1; sample <= 3; sample++) {
    await run(process.execPath, [resolve(repositoryRoot, 'frontend/tests/perf.mjs')], {
      QS_URL: `http://127.0.0.1:${webPort}/?theme=dark&path=src%2FQualityStudio.Api%2FProgram.cs`,
      QS_SAMPLE_INDEX: String(sample),
      JOB_RESULTS_DIR: resultsRoot,
    }, `browser-performance-${sample}.log`);
  }
} finally {
  if (stack) await stop(stack);
}

for (let sample = 1; sample <= 3; sample++) {
  await run(process.execPath, [resolve(repositoryRoot, 'frontend/tests/project-switch-perf.mjs')], {
    QS_SAMPLE_INDEX: String(sample),
    QS_API_DLL: resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll'),
    JOB_RESULTS_DIR: resultsRoot,
  }, `project-switch-performance-${sample}.log`);
}

async function run(command, arguments_, environment, logName) {
  const child = spawn(command, arguments_, {
    cwd: repositoryRoot,
    env: { ...process.env, ...environment },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '';
  child.stdout.on('data', chunk => output += chunk);
  child.stderr.on('data', chunk => output += chunk);
  const code = await new Promise((resolvePromise, rejectPromise) => {
    child.once('error', rejectPromise);
    child.once('exit', exitCode => resolvePromise(exitCode ?? 1));
  });
  await writeFile(resolve(resultsRoot, logName), output);
  if (code !== 0) throw new Error(`${logName} failed with exit code ${code}.`);
}

function version(command, arguments_) {
  return spawnSync(command, arguments_, { encoding: 'utf8' }).stdout.trim();
}

function capture(stream, channel) {
  stream.setEncoding('utf8');
  stream.on('data', chunk => {
    for (const line of chunk.split(/\r?\n/).filter(Boolean)) {
      stackOutput.push(`${channel}: ${line}`);
      if (stackOutput.length > 100) stackOutput.shift();
    }
  });
}

async function waitForReady(child, timeoutMs) {
  await new Promise((resolvePromise, rejectPromise) => {
    const timeout = setTimeout(() => rejectPromise(new Error(
      `Timed out waiting for canary stack.\n${stackOutput.join('\n')}`)), timeoutMs);
    child.stdout.on('data', chunk => {
      if (!chunk.includes('ready:')) return;
      clearTimeout(timeout);
      resolvePromise();
    });
    child.once('exit', code => {
      clearTimeout(timeout);
      rejectPromise(new Error(`Canary stack exited with ${code}.\n${stackOutput.join('\n')}`));
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
  try { process.kill(-child.pid, 'SIGINT'); } catch { child.kill('SIGINT'); }
  await Promise.race([
    new Promise(resolvePromise => child.once('exit', resolvePromise)),
    new Promise(resolvePromise => setTimeout(resolvePromise, 5000)),
  ]);
  if (child.exitCode === null) child.kill('SIGKILL');
}
