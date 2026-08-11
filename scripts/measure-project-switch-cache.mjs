import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const apiDll = process.env.QS_API_DLL ||
  resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const targetRepository = process.env.QS_AGENT_STUDIO_REPOSITORY || '/home/agent/runner-work/PROJ-002/repo';
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const hostRoot = await mkdtemp(resolve(tmpdir(), 'qs-switch-cache-'));
const children = [];

try {
  await mkdir(resolve(hostRoot, '.quality-studio'), { recursive: true });
  await mkdir(resultsRoot, { recursive: true });
  await writeFile(resolve(hostRoot, '.quality-studio', 'repositories.json'), JSON.stringify([{
    id: 'default',
    displayName: 'Agent Studio',
    rootPath: targetRepository,
    globalInputsDirectory: null,
    inputBudgetCharacters: 12_000,
    enabledReviewKinds: ['code', 'security', 'performance'],
    sensors: null,
    archived: false,
    defaultReviewTokenCap: 100_000,
    defaultReviewCostCap: null,
  }], null, 2));

  const coldProcess = await startApi();
  const coldSwitch = await measureSwitch(coldProcess.url);
  await waitForEvent(coldProcess, 'qs.repository.prewarm', 90_000);
  const coldPrewarm = parseEvent(coldProcess.lines, 'qs.repository.prewarm');
  const warmSwitches = [];
  for (let index = 0; index < 3; index++) warmSwitches.push(await measureSwitch(coldProcess.url));
  const sensorCold = await measureRequest(`${coldProcess.url}/api/sensors`);
  const sensorWarm = await measureRequest(`${coldProcess.url}/api/sensors`);
  await stop(coldProcess.child);

  const restoredProcess = await startApi();
  const restoredSwitch = await measureSwitch(restoredProcess.url);
  await waitForEvent(restoredProcess, 'qs.repository.prewarm', 90_000);
  const restoredPrewarm = parseEvent(restoredProcess.lines, 'qs.repository.prewarm');
  const restoredWarmSwitch = await measureSwitch(restoredProcess.url);
  await stop(restoredProcess.child);

  const result = {
    measuredAt: new Date().toISOString(),
    environment: {
      commit: command('git', ['-C', repositoryRoot, 'rev-parse', 'HEAD']),
      targetRepository,
      targetHead: command('git', ['-C', targetRepository, 'rev-parse', 'HEAD']),
      trackedFiles: Number(command('bash', ['-lc', 'git -C "$1" ls-files -z | tr -cd "\\0" | wc -c', 'bash', targetRepository])),
      dotnet: command('dotnet', ['--version']),
      node: process.version,
    },
    before: {
      source: 'QS-59 performance dossier, 2026-08-11, Agent Taskboard / Agent Studio repository at 9af1a848',
      coldProcessToProjectAndTreeMedianMs: 12_309.79,
      warmProjectAndTreeMedianMs: 1_105.95,
      warmProjectAndTreeP95Ms: 1_846.68,
    },
    after: {
      cold: {
        processToHealthMs: round(coldProcess.healthMs),
        processToProjectAndTreeMs: round(coldProcess.healthMs + coldSwitch.durationMs),
        switch: coldSwitch,
        prewarm: coldPrewarm,
      },
      warm: {
        samples: warmSwitches,
        summary: summarize(warmSwitches.map(sample => sample.durationMs)),
      },
      restoredStartup: {
        processToHealthMs: round(restoredProcess.healthMs),
        processToProjectAndTreeMs: round(restoredProcess.healthMs + restoredSwitch.durationMs),
        firstSwitch: restoredSwitch,
        warmSwitch: restoredWarmSwitch,
        prewarm: restoredPrewarm,
      },
      sensors: { firstRequest: sensorCold, warmRequest: sensorWarm },
    },
  };
  await writeFile(resolve(resultsRoot, 'agent-studio-switch-cache.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} finally {
  await Promise.allSettled(children.map(stop));
  await rm(hostRoot, { recursive: true, force: true });
}

async function startApi() {
  const port = await freePort();
  const lines = [];
  const started = performance.now();
  const child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', hostRoot], {
    cwd: hostRoot,
    env: {
      ...process.env,
      QualityStudio__RepositoryRoot: targetRepository,
      QualityStudio__AllowedRoots__0: dirname(targetRepository),
      QualityStudio__Security__Mode: 'Local',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  for (const stream of [child.stdout, child.stderr]) collectLines(stream, lines);
  await waitForHttp(`http://127.0.0.1:${port}/health`, 90_000);
  return { child, lines, url: `http://127.0.0.1:${port}`, healthMs: performance.now() - started };
}

async function measureSwitch(url) {
  const started = performance.now();
  const [project, tree] = await Promise.all([
    measureRequest(`${url}/api/project`),
    measureRequest(`${url}/api/tree?path=`),
  ]);
  return { durationMs: round(performance.now() - started), project, tree };
}

async function measureRequest(url) {
  const started = performance.now();
  const response = await fetch(url);
  const body = await response.arrayBuffer();
  if (!response.ok) throw new Error(`${url} returned ${response.status}`);
  return {
    durationMs: round(performance.now() - started),
    bytes: body.byteLength,
    serverTiming: response.headers.get('server-timing'),
  };
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

function parseEvent(lines, event) {
  const line = [...lines].reverse().find(candidate => candidate.includes(`"event":"${event}"`));
  if (!line) return null;
  const start = line.indexOf('{"event"');
  if (start < 0) return { raw: line };
  try { return JSON.parse(line.slice(start)); } catch { return { raw: line }; }
}

async function waitForEvent(host, event, timeoutMs) {
  await waitFor(() => host.lines.some(line => line.includes(`"event":"${event}"`)), timeoutMs, event);
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
    await delay(20);
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

function summarize(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return {
    count: sorted.length,
    min: round(sorted[0]),
    median: round(sorted[Math.floor(sorted.length / 2)]),
    max: round(sorted.at(-1)),
  };
}

function round(value) {
  return Number(value.toFixed(2));
}
