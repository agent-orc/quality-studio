#!/usr/bin/env node

import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { request as httpRequest } from 'node:http';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const apiDll = process.env.QS_API_DLL
  || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const sourceRepository = process.env.QS_AGENT_STUDIO_REPOSITORY
  || '/home/agent/runner-work/PROJ-002/repo';
const outputRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const temporaryRoot = await mkdtemp(join(tmpdir(), 'qs-78-conditional-switch-'));
let apiProcess;

try {
  await mkdir(outputRoot, { recursive: true });
  const targetRepository = join(temporaryRoot, 'agent-studio');
  run('git', ['clone', '--quiet', '--no-hardlinks', sourceRepository, targetRepository]);

  const contentRoot = join(temporaryRoot, 'host');
  await mkdir(join(contentRoot, '.quality-studio'), { recursive: true });
  await writeFile(join(contentRoot, '.quality-studio', 'repositories.json'), JSON.stringify([{
    id: 'agent-studio',
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

  const port = await freePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const processStarted = performance.now();
  apiProcess = spawn('dotnet', [apiDll, '--urls', baseUrl, '--contentRoot', contentRoot], {
    cwd: contentRoot,
    env: {
      ...process.env,
      DOTNET_ENVIRONMENT: 'Production',
      QualityStudio__RepositoryRoot: targetRepository,
      QualityStudio__AllowedRoots__0: temporaryRoot,
      QualityStudio__Security__Mode: 'Local',
    },
    stdio: 'ignore',
  });
  await waitForHealth(`${baseUrl}/health`, 90_000);
  const processToHealthMs = performance.now() - processStarted;

  const cold = await switchRepository(baseUrl);
  const warmSamples = [];
  let etags = cold.etags;
  for (let index = 0; index < 7; index += 1) {
    const sample = await switchRepository(baseUrl, etags);
    warmSamples.push(sample);
    etags = sample.etags;
  }

  const warmSummary = summarize(warmSamples.map(sample => sample.durationMs));
  const result = {
    measuredAt: new Date().toISOString(),
    slice: 'P-1 conditional project/tree requests',
    environment: {
      qualityStudioBaseCommit: run('git', ['-C', repositoryRoot, 'rev-parse', 'HEAD']),
      qualityStudioWorktreeDirty: run('git', ['-C', repositoryRoot, 'status', '--porcelain']).length > 0,
      sourceRepository,
      targetHead: run('git', ['-C', targetRepository, 'rev-parse', 'HEAD']),
      trackedFiles: Number(run('git', ['-C', targetRepository, 'ls-files']).split('\n').filter(Boolean).length),
      dotnet: run('dotnet', ['--version']),
      node: process.version,
      build: 'Release',
    },
    before: {
      source: 'QS-59 performance dossier measured 2026-08-24',
      coldSnapshotConstructionMs: 23_763.55,
      warmSwitchMedianMs: 664.89,
      warmTreeBytes: 35_519_812,
    },
    after: {
      cold: {
        processToHealthMs: round(processToHealthMs),
        processToUsableMs: round(processToHealthMs + cold.durationMs),
        ...cold,
      },
      warm: { summary: warmSummary, samples: warmSamples },
    },
    acceptance: {
      allWarmResponsesNotModified: warmSamples.every(sample =>
        sample.project.status === 304 && sample.tree.status === 304),
      allWarmTreeBodiesEmpty: warmSamples.every(sample => sample.tree.bytes === 0),
      warmBackendUnder100Ms: warmSummary.medianMs < 100,
    },
  };

  await writeFile(join(outputRoot, 'agent-studio-conditional-switch.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} finally {
  await stop(apiProcess);
  await rm(temporaryRoot, { recursive: true, force: true });
}

async function switchRepository(baseUrl, etags = {}) {
  const started = performance.now();
  const [project, tree] = await Promise.all([
    get(`${baseUrl}/api/repos/agent-studio/project`, etags.project),
    get(`${baseUrl}/api/repos/agent-studio/tree?path=`, etags.tree),
  ]);
  return {
    durationMs: round(performance.now() - started),
    project,
    tree,
    etags: { project: project.etag ?? etags.project, tree: tree.etag ?? etags.tree },
  };
}

function get(urlValue, etag) {
  const url = new URL(urlValue);
  const started = performance.now();
  return new Promise((resolvePromise, rejectPromise) => {
    const request = httpRequest({
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      headers: etag ? { 'If-None-Match': etag } : {},
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => resolvePromise({
        status: response.statusCode,
        durationMs: round(performance.now() - started),
        bytes: Buffer.concat(chunks).byteLength,
        etag: response.headers.etag ?? null,
      }));
    });
    request.setTimeout(90_000, () => request.destroy(new Error(`Timed out requesting ${url}`)));
    request.on('error', rejectPromise);
    request.end();
  });
}

async function waitForHealth(url, timeoutMs) {
  const started = performance.now();
  while (performance.now() - started < timeoutMs) {
    try {
      if ((await get(url)).status === 200) return;
    } catch {
      // The process may not have bound the port yet.
    }
    await delay(25);
  }
  throw new Error(`API did not become healthy within ${timeoutMs} ms`);
}

function freePort() {
  return new Promise((resolvePromise, rejectPromise) => {
    const server = createServer();
    server.once('error', rejectPromise);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = typeof address === 'object' && address ? address.port : 0;
      server.close(() => resolvePromise(port));
    });
  });
}

async function stop(child) {
  if (!child || child.exitCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(resolvePromise => child.once('exit', resolvePromise)), delay(5_000)]);
  if (child.exitCode === null) child.kill('SIGKILL');
}

function run(executable, arguments_) {
  const result = spawnSync(executable, arguments_, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) throw new Error(`${executable} failed: ${result.stderr}`);
  return result.stdout.trim();
}

function summarize(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return {
    count: sorted.length,
    minMs: round(sorted[0]),
    medianMs: round(sorted[Math.floor(sorted.length / 2)]),
    maxMs: round(sorted.at(-1)),
  };
}

function round(value) {
  return Number(value.toFixed(2));
}
