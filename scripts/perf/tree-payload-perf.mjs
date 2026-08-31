#!/usr/bin/env node
// Measures the repository-switch request pair through the same conditional client state used by
// QualityApi: Promise.all([GET /api/project, GET /api/tree?path=]). The retained tree validator
// is scoped to (repositoryId, path), and 304 responses reuse the retained body.
//
// Usage:
//   node scripts/perf/tree-payload-perf.mjs \
//     --targets "taskboard=/path/to/agent-studio" --ref ffe887a0 --out <results-dir>

import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { existsSync } from 'node:fs';
import { appendFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '../..');
const options = parseArguments(process.argv.slice(2));
const apiDll = process.env.QS_API_DLL
  || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const outputRoot = options.out || process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const samples = Number(options.samples || 7);
const children = [];
let temporaryRoot = null;

try {
  if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built for Release: ${apiDll}`);
  if (options.targets.length === 0) throw new Error('Pass --targets "id=/path/to/repository".');
  await mkdir(outputRoot, { recursive: true });
  temporaryRoot = await mkdtemp(join(tmpdir(), 'qs-tree-payload-'));

  const targets = options.targets.map(target => cloneTarget(target, options.ref));
  const host = await startApi(targets);
  const results = [];
  try {
    for (const target of targets) results.push(await measureTarget(host, target));
  } finally {
    await stop(host.child);
  }

  const record = {
    measuredAt: new Date().toISOString(),
    slice: 'QS-78 P-1 — conditional repository-switch requests',
    method: 'real QualityStudio.Api Release; client-path project+tree pair; throwaway repository clone',
    apiDll,
    samples,
    targets: results,
  };
  await writeFile(join(outputRoot, 'perf-tree-payload.json'), JSON.stringify(record, null, 2));
  console.log(JSON.stringify(record, null, 2));

  if (results.some(result => result.warmAfter.status.tree !== 304
      || result.warmAfter.bytes.tree !== 0
      || result.invalidation.status.tree !== 200)) process.exitCode = 1;
} finally {
  await Promise.allSettled(children.map(stop));
  if (temporaryRoot) await rm(temporaryRoot, { recursive: true, force: true });
}

async function measureTarget(host, target) {
  const base = `${host.base}/api/repos/${encodeURIComponent(target.id)}`;

  // Warm the backend snapshot before measuring client cache behavior.
  await Promise.all([request(`${base}/project`), request(`${base}/tree?path=`)]);

  const client = createSwitchClient(base);
  const cold = await client.switchRepository('');
  const before = [];
  const after = [];
  for (let index = 0; index < samples; index += 1) {
    before.push(await client.switchRepository('', false));
    after.push(await client.switchRepository('', true));
  }

  // A new HEAD must invalidate both validators and replace the retained client snapshots.
  const changedFile = git(['-C', target.path, 'ls-files']).split('\n').find(Boolean);
  if (!changedFile) throw new Error(`Target ${target.id} has no tracked files.`);
  const markerPath = resolve(target.path, changedFile);
  const original = await readFile(markerPath);
  await appendFile(markerPath, '\n');
  gitChecked(['-C', target.path, 'add', '--', changedFile]);
  gitChecked(['-C', target.path, '-c', 'user.name=Quality Studio Perf',
    '-c', 'user.email=perf@example.invalid', 'commit', '--quiet', '-m', 'Invalidate snapshot for perf check']);
  const invalidation = await client.switchRepository('', true);
  await writeFile(markerPath, original);

  return {
    repositoryId: target.id,
    sourcePath: target.sourcePath,
    head: target.head,
    trackedFiles: target.trackedFiles,
    cold: describe([cold]),
    warmBefore: describe(before),
    warmAfter: describe(after),
    invalidation: describe([invalidation]),
  };
}

function createSwitchClient(base) {
  const projectSnapshots = new Map();
  const treeSnapshots = new Map();
  return {
    async switchRepository(path, conditional = true) {
      const treeKey = JSON.stringify(['target', path]);
      const retainedProject = projectSnapshots.get('target');
      const retainedTree = treeSnapshots.get(treeKey);
      const started = performance.now();
      const [project, tree] = await Promise.all([
        request(`${base}/project`, conditional ? retainedProject?.etag : null),
        request(`${base}/tree?path=${encodeURIComponent(path)}`, conditional ? retainedTree?.etag : null),
      ]);
      retain(projectSnapshots, 'target', project, retainedProject);
      retain(treeSnapshots, treeKey, tree, retainedTree);
      return { criticalPathMs: round(performance.now() - started), project, tree };
    },
  };
}

function retain(store, key, response, retained) {
  if (response.status === 200) store.set(key, { etag: response.etag, bytes: response.bytes });
  else if (response.status === 304 && !retained) throw new Error(`Received 304 without a retained snapshot for ${key}.`);
  else if (response.status !== 304) throw new Error(`Unexpected ${response.status} for ${response.url}.`);
}

async function request(url, etag = null) {
  const started = performance.now();
  const response = await fetch(url, { headers: etag ? { 'If-None-Match': etag } : {} });
  const body = await response.arrayBuffer();
  return {
    url,
    status: response.status,
    totalMs: round(performance.now() - started),
    bytes: body.byteLength,
    etag: response.headers.get('etag'),
  };
}

function describe(runs) {
  return {
    criticalPathMs: statistics(runs.map(run => run.criticalPathMs)),
    projectMs: statistics(runs.map(run => run.project.totalMs)),
    treeMs: statistics(runs.map(run => run.tree.totalMs)),
    status: { project: runs.at(-1).project.status, tree: runs.at(-1).tree.status },
    bytes: { project: runs.at(-1).project.bytes, tree: runs.at(-1).tree.bytes },
  };
}

function statistics(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return {
    median: sorted[Math.floor(sorted.length / 2)],
    min: sorted[0],
    max: sorted.at(-1),
    samples: sorted.length,
  };
}

function cloneTarget(target, ref) {
  const path = resolve(temporaryRoot, `repo-${target.id}`);
  gitChecked(['clone', '--quiet', '--no-hardlinks', target.path, path]);
  if (ref) gitChecked(['-C', path, 'checkout', '--quiet', '--detach', ref]);
  return {
    ...target,
    path,
    sourcePath: target.path,
    trackedFiles: git(['-C', path, 'ls-files']).split('\n').filter(Boolean).length,
    head: git(['-C', path, 'rev-parse', '--short', 'HEAD']).trim(),
  };
}

async function startApi(targets) {
  const contentRoot = resolve(temporaryRoot, 'api-host');
  await mkdir(resolve(contentRoot, '.quality-studio'), { recursive: true });
  await writeFile(resolve(contentRoot, '.quality-studio/repositories.json'), JSON.stringify(
    targets.map(target => ({
      id: target.id, displayName: target.id, rootPath: target.path, globalInputsDirectory: null,
      inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'],
      sensors: null, archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
    })), null, 2));
  const port = await freePort();
  const child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', contentRoot], {
    cwd: contentRoot,
    env: {
      ...process.env,
      DOTNET_ENVIRONMENT: 'Production',
      QualityStudio__RepositoryRoot: targets[0].path,
      QualityStudio__AllowedRoots__0: temporaryRoot,
      QualityStudio__Security__Mode: 'Local',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  child.stdout.resume();
  child.stderr.resume();
  const base = `http://127.0.0.1:${port}`;
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      if ((await fetch(`${base}/health`)).ok) return { child, base };
    } catch { /* The listener is not ready yet. */ }
    await delay(50);
  }
  throw new Error('The API did not become healthy.');
}

function git(arguments_) {
  return spawnSync('git', arguments_, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).stdout ?? '';
}

function gitChecked(arguments_) {
  const result = spawnSync('git', arguments_, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`git ${arguments_.join(' ')} failed: ${result.stderr}`);
  return result.stdout ?? '';
}

function freePort() {
  return new Promise((done, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => done(typeof address === 'object' && address ? address.port : 0));
    });
  });
}

async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(done => child.once('exit', done)), delay(5_000)]);
  if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
}

function round(value) {
  return Number.isFinite(value) ? Math.round(value * 100) / 100 : null;
}

function parseArguments(argv) {
  const parsed = { out: null, ref: null, samples: 7, targets: [] };
  for (let index = 0; index < argv.length; index += 1) {
    if (argv[index] === '--out') parsed.out = resolve(argv[++index]);
    else if (argv[index] === '--ref') parsed.ref = argv[++index];
    else if (argv[index] === '--samples') parsed.samples = Number(argv[++index]);
    else if (argv[index] === '--targets') {
      for (const entry of argv[++index].split(',')) {
        const separator = entry.indexOf('=');
        parsed.targets.push({ id: entry.slice(0, separator), path: resolve(entry.slice(separator + 1)) });
      }
    }
  }
  return parsed;
}
