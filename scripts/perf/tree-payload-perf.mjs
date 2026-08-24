#!/usr/bin/env node
// Quality Studio tree-payload probe (QS-59).
//
// `/api/tree?path=` is the second half of the repository-switch critical path and,
// unlike `/api/project`, it has no projection cache and no phase telemetry. This probe
// records what the endpoint actually returns so the cost can be attributed:
//   * payload bytes and node count per hierarchy level,
//   * warm repeat latency,
//   * the conditional-request (ETag / 304) path the frontend does not use today,
//   * a single-module subtree request as the bounded alternative.
//
// Usage: node scripts/perf/tree-payload-perf.mjs --targets "a=/path/a,b=/path/b" [--out <dir>]

import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { request as httpRequest } from 'node:http';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDir, '../..');
const options = parseArguments(process.argv.slice(2));
const apiDll = process.env.QS_API_DLL
  || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const outputRoot = options.out || process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'perf-out');
const children = [];
let tempRoot = null;

try {
  if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built. Expected ${apiDll}`);
  await mkdir(outputRoot, { recursive: true });
  tempRoot = await mkdtemp(join(tmpdir(), 'qs-tree-payload-'));
  const targets = (options.targets.length > 0 ? options.targets : [
    { id: 'default', displayName: 'Quality Studio', path: repositoryRoot },
  ]).map(target => {
    const path = resolve(tempRoot, `repo-${target.id}`);
    git(['clone', '--quiet', '--no-hardlinks', target.path, path]);
    return {
      ...target,
      path,
      sourcePath: target.path,
      trackedFiles: git(['-C', path, 'ls-files']).split('\n').filter(Boolean).length,
      head: git(['-C', path, 'rev-parse', '--short', 'HEAD']).trim(),
    };
  });

  const host = await startApi(targets);
  const results = [];
  try {
    for (const target of targets) {
      // Warm the snapshot so the measurement isolates projection and transfer, not the scan.
      await get(host, `/api/repos/${target.id}/tree?path=`);
      const warm = [];
      for (let index = 0; index < 5; index += 1) {
        warm.push(await get(host, `/api/repos/${target.id}/tree?path=`));
      }
      const full = warm[warm.length - 1];
      const nodes = countNodes(full.json?.nodes ?? []);
      const conditional = await get(host, `/api/repos/${target.id}/tree?path=`,
        { 'if-none-match': full.etag ?? '' });
      const firstModule = findFirst(full.json?.nodes ?? [], 'module');
      const subtree = firstModule
        ? await get(host, `/api/repos/${target.id}/tree?path=${encodeURIComponent(firstModule.path)}`)
        : null;
      const project = await get(host, `/api/repos/${target.id}/project`);

      results.push({
        repositoryId: target.id,
        sourcePath: target.sourcePath,
        head: target.head,
        trackedFiles: target.trackedFiles,
        fullTree: {
          medianMs: median(warm.map(sample => sample.totalMs)),
          samplesMs: warm.map(sample => sample.totalMs),
          bytes: full.bytes,
          bytesPerTrackedFile: Math.round(full.bytes / target.trackedFiles),
          nodes,
        },
        conditionalRequest: { status: conditional.status, totalMs: conditional.totalMs, bytes: conditional.bytes },
        singleModuleSubtree: subtree
          ? { path: firstModule.path, status: subtree.status, totalMs: subtree.totalMs, bytes: subtree.bytes }
          : null,
        projectDashboard: { totalMs: project.totalMs, bytes: project.bytes },
      });
    }
  } finally {
    await stop(host.child);
  }

  const record = { measuredAt: new Date().toISOString(), apiDll, build: 'Release', targets: results };
  await writeFile(join(outputRoot, 'perf-tree-payload.json'), JSON.stringify(record, null, 2));
  console.log(JSON.stringify(record, null, 2));
} finally {
  await Promise.allSettled(children.map(stop));
  if (tempRoot) await rm(tempRoot, { recursive: true, force: true });
}

function countNodes(nodes) {
  const perLevel = {};
  const filePaths = new Map();
  let total = 0;
  const walk = list => {
    for (const node of list) {
      total += 1;
      perLevel[node.level] = (perLevel[node.level] ?? 0) + 1;
      if (node.level === 'file') filePaths.set(node.path, (filePaths.get(node.path) ?? 0) + 1);
      if (node.children?.length) walk(node.children);
    }
  };
  walk(nodes);
  const repeated = [...filePaths.entries()].filter(([, count]) => count > 1);
  return {
    total,
    perLevel,
    distinctFilePaths: filePaths.size,
    filePathsEmittedMoreThanOnce: repeated.length,
    worstRepeatedFilePath: repeated.sort((left, right) => right[1] - left[1])[0] ?? null,
  };
}

function findFirst(nodes, level) {
  for (const node of nodes) {
    if (node.level === level) return node;
    const nested = findFirst(node.children ?? [], level);
    if (nested) return nested;
  }
  return null;
}

function get(host, path, extraHeaders = {}) {
  const started = performance.now();
  const url = new URL(`${host.base}${path}`);
  return new Promise(done => {
    const request = httpRequest({
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method: 'GET',
      headers: { accept: 'application/json', 'X-Client-Id': 'qs-59-perf', ...extraHeaders },
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        let json = null;
        try { json = JSON.parse(text); } catch { json = null; }
        done({
          status: response.statusCode,
          totalMs: round(performance.now() - started),
          bytes: Buffer.byteLength(text),
          etag: response.headers.etag ?? null,
          json,
        });
      });
    });
    request.setTimeout(600_000, () => request.destroy(new Error('timeout')));
    request.on('error', error => done({ status: 0, totalMs: round(performance.now() - started), bytes: 0, etag: null, json: { error: error.message } }));
    request.end();
  });
}

async function startApi(targets) {
  const contentRoot = resolve(tempRoot, 'api-host');
  await mkdir(join(contentRoot, '.quality-studio'), { recursive: true });
  await writeFile(join(contentRoot, '.quality-studio/repositories.json'), JSON.stringify(
    targets.map(target => ({
      id: target.id,
      displayName: target.displayName,
      rootPath: target.path,
      globalInputsDirectory: null,
      inputBudgetCharacters: 12000,
      enabledReviewKinds: ['code', 'security', 'performance'],
      sensors: null,
      archived: false,
      defaultReviewTokenCap: 100000,
      defaultReviewCostCap: null,
    })), null, 2));
  const port = await freePort();
  const child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', contentRoot], {
    cwd: contentRoot,
    env: {
      ...process.env,
      DOTNET_ENVIRONMENT: 'Production',
      QualityStudio__RepositoryRoot: targets[0].path,
      QualityStudio__AllowedRoots__0: '/',
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
    const health = await get({ base }, '/health');
    if (health.status === 200) return { child, base };
    await delay(50);
  }
  throw new Error('the API did not become healthy');
}

function git(args) {
  const result = spawnSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return result.stdout ?? '';
}

function freePort() {
  return new Promise((done, reject) => {
    const server = createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => done(port));
    });
  });
}

async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(done => child.once('exit', done)), delay(5_000)]);
  if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
}

function median(values) {
  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return round(sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2);
}

function round(value) {
  return Number.isFinite(value) ? Math.round(value * 100) / 100 : null;
}

function parseArguments(argv) {
  const parsed = { out: null, targets: [] };
  for (let index = 0; index < argv.length; index += 1) {
    if (argv[index] === '--out') parsed.out = resolve(argv[++index]);
    else if (argv[index] === '--targets') {
      for (const entry of argv[++index].split(',')) {
        const [id, path] = entry.split('=');
        parsed.targets.push({ id, displayName: id, path: resolve(path) });
      }
    }
  }
  return parsed;
}
