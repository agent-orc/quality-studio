import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';

const repositoryRoot = resolve(new URL('..', import.meta.url).pathname);
const targetRoot = resolve(process.env.QS_PERF_REPOSITORY ?? '/home/agent/runner-work/PROJ-002/repo');
const resultsRoot = resolve(process.env.JOB_RESULTS_DIR ?? resolve(repositoryRoot, 'results'));
const apiDll = resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const samples = Number(process.env.QS_PERF_SAMPLES ?? 10);
let child;
let tempRoot;

try {
  await mkdir(resultsRoot, { recursive: true });
  tempRoot = await mkdtemp(resolve(tmpdir(), 'qs-tree-transport-'));
  await mkdir(resolve(tempRoot, '.quality-studio'), { recursive: true });
  await writeFile(resolve(tempRoot, '.quality-studio/repositories.json'), JSON.stringify([{
    id: 'default', displayName: basename(targetRoot), rootPath: targetRoot,
    globalInputsDirectory: null, inputBudgetCharacters: 12000,
    enabledReviewKinds: ['code', 'security', 'performance'], sensors: null, archived: false,
    defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
  }], null, 2));
  const port = await freePort();
  const lines = [];
  child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', tempRoot], {
    cwd: tempRoot,
    env: {
      ...process.env,
      QualityStudio__RepositoryRoot: targetRoot,
      QualityStudio__AllowedRoots__0: resolve(targetRoot, '..'),
      QualityStudio__Security__Mode: 'Local',
      Logging__LogLevel__Default: 'Information',
      Logging__LogLevel__Microsoft_AspNetCore: 'Warning',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  collect(child.stdout, lines);
  collect(child.stderr, lines);
  await waitForHttp(`http://127.0.0.1:${port}/health`, 30_000);
  await waitFor(() => lines.some(line => line.includes('qs.repository.prewarm')), 180_000, 'repository prewarm');

  const base = `http://127.0.0.1:${port}`;
  const coldProjection = await timedFetch(`${base}/api/tree/v2?limit=500`);
  const rootPayload = JSON.parse(coldProjection.text);
  const expandable = rootPayload.nodes.find(node => node.hasChildren);
  if (!expandable) throw new Error('The measured repository has no expandable root node.');
  const childUrl = `${base}/api/tree/v2?limit=500&parentId=${encodeURIComponent(expandable.id)}` +
    `&snapshot=${encodeURIComponent(rootPayload.snapshotEtag)}`;
  await timedFetch(childUrl);
  await timedFetch(`${base}/api/project`);

  const v1Tree = [];
  const v2Root = [];
  const v1Switch = [];
  const v2Switch = [];
  const cachedExpand = [];
  for (let index = 0; index < samples; index++) {
    v1Tree.push(await timedFetch(`${base}/api/tree?path=`));
    v2Root.push(await timedFetch(`${base}/api/tree/v2?limit=500`));
    v1Switch.push(await concurrentFetch(`${base}/api/project`, `${base}/api/tree?path=`));
    v2Switch.push(await concurrentFetch(`${base}/api/project`, `${base}/api/tree/v2?limit=500`));
    cachedExpand.push(await timedFetch(childUrl));
  }

  const conditionalStarted = performance.now();
  const conditionalResponse = await fetch(`${base}/api/tree/v2?limit=500`, {
    headers: { 'If-None-Match': coldProjection.etag },
  });
  await conditionalResponse.arrayBuffer();
  const result = {
    measuredAt: new Date().toISOString(),
    environment: {
      productCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repositoryRoot, encoding: 'utf8' }).trim(),
      productWorktreeDirty: gitValue(repositoryRoot, ['status', '--porcelain']).length > 0,
      targetRoot,
      targetCommit: gitValue(targetRoot, ['rev-parse', 'HEAD']),
      trackedFiles: Number(gitValue(targetRoot, ['ls-files']).split('\n').filter(Boolean).length),
      dotnet: execFileSync('dotnet', ['--version'], { encoding: 'utf8' }).trim(),
      node: process.version,
      samples,
    },
    dossierBaseline: {
      source: 'QS-59 dossier refreshed 2026-08-12 on the same repository commit',
      recursiveRootBytes: 29119333,
      warmProjectAndTreeMedianMs: 423.74,
      warmProjectAndTreeP95Ms: 954.10,
    },
    sameBuildBefore: summarize(v1Tree),
    after: {
      coldProjection: select(coldProjection),
      root: summarize(v2Root),
      cachedExpand: summarize(cachedExpand),
      conditional: {
        status: conditionalResponse.status,
        durationMs: round(performance.now() - conditionalStarted),
      },
    },
    switch: {
      recursiveV1: summarize(v1Switch),
      lazyV2: summarize(v2Switch),
    },
    reduction: {
      rootBytesPercent: reduction(median(v1Tree.map(item => item.bytes)), median(v2Root.map(item => item.bytes))),
      rootMedianPercent: reduction(median(v1Tree.map(item => item.durationMs)), median(v2Root.map(item => item.durationMs))),
      switchMedianPercent: reduction(median(v1Switch.map(item => item.durationMs)), median(v2Switch.map(item => item.durationMs))),
    },
    transportEvents: lines.filter(line => line.includes('qs.tree.transport')).slice(-20),
  };
  await writeFile(resolve(resultsRoot, 'tree-transport-benchmark.json'), `${JSON.stringify(result, null, 2)}\n`);
  console.log(JSON.stringify(result, null, 2));
} finally {
  if (child && child.exitCode === null) {
    child.kill('SIGTERM');
    await Promise.race([new Promise(resolvePromise => child.once('exit', resolvePromise)), delay(5_000)]);
    if (child.exitCode === null) child.kill('SIGKILL');
  }
  if (tempRoot) await rm(tempRoot, { recursive: true, force: true });
}

async function timedFetch(url) {
  const started = performance.now();
  const response = await fetch(url);
  const buffer = await response.arrayBuffer();
  if (!response.ok) throw new Error(`${url} failed: ${response.status} ${new TextDecoder().decode(buffer)}`);
  return {
    durationMs: round(performance.now() - started),
    bytes: buffer.byteLength,
    status: response.status,
    etag: response.headers.get('etag'),
    serverTiming: response.headers.get('server-timing'),
    text: new TextDecoder().decode(buffer),
  };
}

async function concurrentFetch(projectUrl, treeUrl) {
  const started = performance.now();
  const [project, tree] = await Promise.all([timedFetch(projectUrl), timedFetch(treeUrl)]);
  return {
    durationMs: round(performance.now() - started),
    bytes: project.bytes + tree.bytes,
    projectMs: project.durationMs,
    treeMs: tree.durationMs,
  };
}

function summarize(values) {
  const durations = values.map(value => value.durationMs);
  const bytes = values.map(value => value.bytes);
  return {
    samples: values.length,
    medianMs: median(durations),
    p95Ms: percentile(durations, 0.95),
    minMs: Math.min(...durations),
    maxMs: Math.max(...durations),
    medianBytes: median(bytes),
    serverTiming: values.find(value => value.serverTiming)?.serverTiming ?? null,
  };
}

function select(value) {
  return { durationMs: value.durationMs, bytes: value.bytes, serverTiming: value.serverTiming };
}

function collect(stream, lines) {
  stream.setEncoding('utf8');
  let buffer = '';
  stream.on('data', chunk => {
    buffer += chunk;
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      lines.push(buffer.slice(0, newline).trim());
      buffer = buffer.slice(newline + 1);
    }
  });
}

async function freePort() {
  const server = createServer();
  await new Promise((resolvePromise, rejectPromise) =>
    server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
}

async function waitForHttp(url, timeoutMs) {
  await waitFor(async () => { try { return (await fetch(url)).ok; } catch { return false; } }, timeoutMs, url);
}

async function waitFor(predicate, timeoutMs, label) {
  const started = performance.now();
  while (performance.now() - started < timeoutMs) {
    if (await predicate()) return;
    await delay(50);
  }
  throw new Error(`Timed out waiting for ${label}`);
}

function gitValue(cwd, arguments_) {
  return execFileSync('git', arguments_, { cwd, encoding: 'utf8' }).trim();
}

function percentile(values, quantile) {
  const sorted = [...values].sort((left, right) => left - right);
  return round(sorted[Math.min(sorted.length - 1, Math.ceil(quantile * sorted.length) - 1)]);
}

function median(values) { return percentile(values, 0.5); }
function reduction(before, after) { return round((before - after) / before * 100); }
function round(value) { return Math.round(value * 100) / 100; }
