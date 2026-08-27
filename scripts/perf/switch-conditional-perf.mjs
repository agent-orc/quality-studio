// Measures the repository-switch critical path with and without the conditional request that
// QS-78 slice P-1 introduced on the client. The client issues Promise.all([/api/project,
// /api/tree?path=]); this harness issues exactly that pair against a real QualityStudio.Api
// Release build and a real repository clone, and reports latency and transferred bytes for:
//
//   cold      first switch to the repository — no validator retained yet, full bodies
//   warm-before  re-switch as the client behaved before P-1 — unconditional, full bodies again
//   warm-after   re-switch as the client behaves after P-1 — If-None-Match, 304, zero bytes
//
// Usage: node scripts/perf/switch-conditional-perf.mjs --target <git-repo> [--out <dir>] [--samples 7]
import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const scriptRoot = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptRoot, '../..');
const options = parseArguments(process.argv.slice(2));
const apiDll = process.env.QS_API_DLL || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const resultsRoot = options.out || process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const samples = Number(options.samples || 7);
const state = { children: [], tempRoot: null };

try {
  if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built for Release. Expected ${apiDll}`);
  if (!options.target) throw new Error('Pass --target <path-to-git-repository>');
  const source = resolve(options.target);
  if (!existsSync(source)) throw new Error(`Target repository does not exist: ${source}`);

  state.tempRoot = await mkdtemp(join(tmpdir(), 'qs-switch-conditional-'));
  await mkdir(resultsRoot, { recursive: true });
  const apiHost = resolve(state.tempRoot, 'api-host');
  const clone = resolve(state.tempRoot, 'target');
  await mkdir(apiHost, { recursive: true });

  // Measure a throwaway clone: /api/tree reads .quality/ sidecars, and measuring the live
  // worktree would fold this run's own writes into the Git state the ETag is derived from.
  runGit(repositoryRoot, 'clone', '--quiet', '--no-hardlinks', source, clone);
  const targetCommit = runGitValue(clone, 'rev-parse', '--short', 'HEAD');
  const trackedFiles = runGitValue(clone, 'ls-files').split('\n').filter(Boolean).length;

  await writeRegistry(apiHost, clone);
  const apiPort = await freePort();
  const apiLines = [];
  start('api', 'dotnet', [apiDll, '--urls', `http://127.0.0.1:${apiPort}`, '--contentRoot', apiHost], apiHost, {
    DOTNET_ENVIRONMENT: 'Production',
    QualityStudio__RepositoryRoot: clone,
    QualityStudio__AllowedRoots__0: state.tempRoot,
    QualityStudio__Security__Mode: 'Local',
  }, apiLines);
  await waitForHttp(`http://127.0.0.1:${apiPort}/health`, 60_000);

  // The background prewarmer builds the hierarchy snapshot; without waiting, "cold" would
  // measure snapshot construction rather than the switch the operator sees.
  const base = `http://127.0.0.1:${apiPort}/api/repos/target`;
  await waitFor(() => apiLines.some(line => line.includes('"event":"qs.repository.prewarm"')), 900_000, 'repository prewarm');
  const prewarmEvent = apiLines.find(line => line.includes('"event":"qs.repository.prewarm"'))?.trim() ?? null;

  const cold = await switchPair(base, null);
  const validators = { project: cold.project.etag, tree: cold.tree.etag };
  if (!validators.tree || !validators.project) throw new Error('The API did not return an ETag on the switch pair');

  const before = [];
  const after = [];
  for (let index = 0; index < samples; index += 1) {
    before.push(await switchPair(base, null));
    after.push(await switchPair(base, validators));
  }

  const notModified = after.every(sample => sample.tree.status === 304 && sample.project.status === 304);
  const result = {
    measuredAt: new Date().toISOString(),
    slice: 'QS-78 P-1 — take the conditional path on the switch pair',
    target: { source, commit: targetCommit, trackedFiles },
    samples,
    method: 'real QualityStudio.Api Release, Local mode, throwaway clone, no interception',
    prewarmEvent,
    cold: describe([cold]),
    warmBefore: describe(before),
    warmAfter: describe(after),
    allWarmRequestsNotModified: notModified,
  };
  result.delta = {
    warmMillisecondsSaved: +(result.warmBefore.criticalPathMs.median - result.warmAfter.criticalPathMs.median).toFixed(2),
    warmBytesSaved: result.warmBefore.bytes.total - result.warmAfter.bytes.total,
    speedup: +(result.warmBefore.criticalPathMs.median / Math.max(result.warmAfter.criticalPathMs.median, 0.01)).toFixed(2),
  };

  await writeFile(resolve(resultsRoot, 'switch-conditional-perf.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
  if (!notModified) process.exitCode = 1;
} finally {
  await Promise.allSettled(state.children.map(stop));
  if (state.tempRoot) await rm(state.tempRoot, { recursive: true, force: true });
}

// Issues the client's switch pair. `validators` mirrors what the client retains per repository.
async function switchPair(base, validators) {
  const started = performance.now();
  const [project, tree] = await Promise.all([
    timedGet(`${base}/project`, validators?.project),
    timedGet(`${base}/tree?path=`, validators?.tree),
  ]);
  return { criticalPathMs: +(performance.now() - started).toFixed(2), project, tree };
}

async function timedGet(url, validator) {
  const started = performance.now();
  const response = await fetch(url, { headers: validator ? { 'If-None-Match': validator } : {} });
  const body = await response.arrayBuffer();
  return {
    status: response.status,
    durationMs: +(performance.now() - started).toFixed(2),
    bytes: body.byteLength,
    etag: response.headers.get('etag'),
  };
}

function describe(runs) {
  const pick = selector => summarise(runs.map(selector));
  return {
    criticalPathMs: pick(run => run.criticalPathMs),
    projectMs: pick(run => run.project.durationMs),
    treeMs: pick(run => run.tree.durationMs),
    status: { project: runs[0].project.status, tree: runs[0].tree.status },
    bytes: {
      project: runs[0].project.bytes,
      tree: runs[0].tree.bytes,
      total: runs[0].project.bytes + runs[0].tree.bytes,
    },
  };
}

function summarise(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return {
    median: +sorted[Math.floor(sorted.length / 2)].toFixed(2),
    min: +sorted[0].toFixed(2),
    max: +sorted[sorted.length - 1].toFixed(2),
    samples: sorted.length,
  };
}

function parseArguments(argv) {
  const parsed = {};
  for (let index = 0; index < argv.length; index += 2) {
    if (!argv[index].startsWith('--')) continue;
    parsed[argv[index].slice(2)] = argv[index + 1];
  }
  return parsed;
}

async function writeRegistry(apiHost, target) {
  const registryDirectory = resolve(apiHost, '.quality-studio');
  await mkdir(registryDirectory, { recursive: true });
  await writeFile(resolve(registryDirectory, 'repositories.json'), JSON.stringify([{
    id: 'target', displayName: 'Switch target', rootPath: target, globalInputsDirectory: null,
    inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'],
    sensors: null, archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
  }], null, 2));
}

function runGit(cwd, ...arguments_) {
  const result = spawnSync('git', ['-C', cwd, ...arguments_], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`git ${arguments_.join(' ')} failed: ${result.stderr}`);
  return result.stdout;
}

function runGitValue(cwd, ...arguments_) {
  return runGit(cwd, ...arguments_).trim();
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
        lines.push(buffer.slice(0, newline));
        buffer = buffer.slice(newline + 1);
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
  const port = server.address().port;
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
    await delay(200);
  }
  throw new Error(`Timed out waiting for ${label}`);
}
