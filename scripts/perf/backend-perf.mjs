#!/usr/bin/env node
// Quality Studio backend performance harness (QS-59).
//
// Measures four things against a real QualityStudio.Api process and real Git
// repositories, without a browser and without Playwright interception:
//   1. API startup: process spawn to the first successful /health response.
//   2. Repository switch: the /api/project critical path plus the deferred
//      detail fan-out the frontend issues in QualityApi.selectRepository.
//   3. Review-run latency: estimate, deterministic sensor scans, and the
//      queued review job, split into the phases the API reports.
//   4. Memory growth: resident set size sampled while a long operator session
//      is replayed against the same process.
//
// Usage:
//   node scripts/perf/backend-perf.mjs [--out <dir>] [--targets <a=path,b=path>]
//                                      [--startup-runs 5] [--session-loops 40]
// Results are written as JSON to --out (default: $JOB_RESULTS_DIR or ./perf-out).

import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import { cpus, tmpdir, totalmem } from 'node:os';
import { dirname, resolve, join } from 'node:path';
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
let seedRoot = repositoryRoot;

try {
  if (!existsSync(apiDll)) throw new Error(`QualityStudio.Api is not built. Expected ${apiDll}`);
  await mkdir(outputRoot, { recursive: true });
  tempRoot = await mkdtemp(join(tmpdir(), 'qs-backend-perf-'));

  const configured = options.targets.length > 0 ? options.targets : [
    { id: 'default', displayName: 'Quality Studio', path: repositoryRoot },
  ];
  // Every target is measured on a throwaway clone: review runs and sensors write into
  // `.quality/`, so no source repository is ever mutated by the harness.
  const targets = configured.map(target => cloneTarget(target.id, target.displayName, target.path));
  const churnTarget = cloneTarget('churn', 'Edit-churn clone', configured[0].path);
  seedRoot = targets[0].path;
  for (const target of [...targets, churnTarget]) {
    target.trackedFiles = run('git', ['-C', target.path, 'ls-files'], true).split('\n').filter(Boolean).length;
    target.head = run('git', ['-C', target.path, 'rev-parse', '--short', 'HEAD'], true).trim();
  }

  const environmentRecord = {
    measuredAt: new Date().toISOString(),
    host: {
      platform: process.platform,
      kernel: run('uname', ['-r'], true).trim(),
      cpuModel: cpuModel(),
      logicalCpus: cpus().length,
      totalMemoryGiB: Math.round(totalmem() / 2 ** 30),
      dotnet: run('dotnet', ['--version'], true).trim(),
      node: process.version,
    },
    build: 'Release',
    apiDll,
    targets: [...targets, churnTarget].map(({ id, displayName, sourcePath, trackedFiles, head }) =>
      ({ id, displayName, sourcePath, trackedFiles, head })),
  };

  const results = { environment: environmentRecord };

  // ---- 1. startup ---------------------------------------------------------
  if (!options.stages || options.stages.includes('startup')) {
    results.startup = await measureStartup(targets, options.startupRuns);
    await writeFile(join(outputRoot, 'perf-startup.json'), JSON.stringify(results.startup, null, 2));
  }

  // ---- 2..4 share one long-lived API process ------------------------------
  const host = await startApi([...targets, churnTarget], 'session');
  try {
    await waitForPrewarm(host, [...targets, churnTarget].map(target => target.id), 300_000);
    // Each stage is isolated so one slow or failing sensor cannot discard the whole measurement.
    const stage = async (name, file, work) => {
      if (options.stages && !options.stages.includes(name)) return;
      try {
        results[name] = await work();
      } catch (error) {
        results[name] = { failed: true, error: error.message };
      }
      await writeFile(join(outputRoot, file), JSON.stringify(results[name], null, 2));
    };
    await stage('switch', 'perf-switch.json', () => measureSwitch(host, targets, options.switchRuns));
    await stage('memory', 'perf-memory.json', () => measureMemory(host, targets, options.sessionLoops));
    await stage('editChurn', 'perf-edit-churn.json', () => measureEditChurn(host, churnTarget, options.churnEdits));
    await stage('review', 'perf-review.json', () => measureReview(host, targets));
    await stage('reviewScaling', 'perf-review-scaling.json', () => measureReviewScaling(host, targets));
    await stage('sensorInvalidation', 'perf-sensor-invalidation.json',
      () => measureSensorInvalidation(host, targets));
    results.prewarmEvents = host.events.filter(event => event.event === 'qs.repository.prewarm');
  } finally {
    await stop(host.child);
  }

  await writeFile(join(outputRoot, 'perf-summary.json'), JSON.stringify(results, null, 2));
  console.log(JSON.stringify(summarise(results), null, 2));
} finally {
  await Promise.allSettled(children.map(stop));
  if (tempRoot) await rm(tempRoot, { recursive: true, force: true });
}

// ---------------------------------------------------------------- measures

async function measureStartup(targets, runs) {
  const samples = { seededSingleRepository: [], allRegisteredRepositories: [] };
  for (const shape of ['seededSingleRepository', 'allRegisteredRepositories']) {
    for (let index = 0; index < runs; index += 1) {
      const host = await startApi(shape === 'seededSingleRepository' ? [] : targets, `startup-${shape}-${index}`);
      samples[shape].push({
        run: index,
        spawnToHealthMs: host.healthMs,
        firstLogMs: host.firstLogMs,
        listeningLogMs: host.listeningMs,
      });
      await stop(host.child);
    }
  }
  return {
    description: 'dotnet process spawn to first HTTP 200 from /health, Release build, no browser.',
    runs,
    samples,
    statistics: {
      seededSingleRepository: statistics(samples.seededSingleRepository.map(sample => sample.spawnToHealthMs)),
      allRegisteredRepositories: statistics(samples.allRegisteredRepositories.map(sample => sample.spawnToHealthMs)),
      listeningLogSingle: statistics(samples.seededSingleRepository.map(sample => sample.listeningLogMs)),
      listeningLogAll: statistics(samples.allRegisteredRepositories.map(sample => sample.listeningLogMs)),
    },
  };
}

async function measureSwitch(host, targets, runs) {
  const perTarget = [];
  for (const target of targets) {
    const warm = [];
    for (let index = 0; index < runs; index += 1) {
      warm.push(await timedJson(host, `/api/repos/${target.id}/project`));
    }
    const critical = [];
    for (let index = 0; index < runs; index += 1) {
      const started = performance.now();
      const [project, tree] = await Promise.all([
        timedJson(host, `/api/repos/${target.id}/project`),
        timedJson(host, `/api/repos/${target.id}/tree?path=`),
      ]);
      critical.push({
        totalMs: round(performance.now() - started),
        projectMs: project.totalMs,
        treeMs: tree.totalMs,
        projectBytes: project.bytes,
        treeBytes: tree.bytes,
        treeServerTiming: tree.serverTiming,
      });
    }
    const detail = [];
    for (let index = 0; index < runs; index += 1) {
      const started = performance.now();
      const parts = await Promise.all([
        timedJson(host, `/api/repos/${target.id}/scan`),
        timedJson(host, `/api/repos/${target.id}/inputs`),
        timedJson(host, `/api/repos/${target.id}/guidelines`),
        timedJson(host, `/api/repos/${target.id}/risk?days=90`),
        timedJson(host, `/api/repos/${target.id}/review/runs`),
        timedJson(host, `/api/repos/${target.id}/usage`),
      ]);
      detail.push({
        totalMs: round(performance.now() - started),
        scanMs: parts[0].totalMs,
        inputsMs: parts[1].totalMs,
        guidelinesMs: parts[2].totalMs,
        riskMs: parts[3].totalMs,
        reviewRunsMs: parts[4].totalMs,
        usageMs: parts[5].totalMs,
      });
    }
    perTarget.push({
      repositoryId: target.id,
      trackedFiles: target.trackedFiles,
      warmProject: {
        samples: warm.map(sample => ({ totalMs: sample.totalMs, serverTiming: sample.serverTiming })),
        statistics: statistics(warm.map(sample => sample.totalMs)),
      },
      criticalPath: { samples: critical, statistics: statistics(critical.map(sample => sample.totalMs)) },
      deferredDetail: {
        samples: detail,
        statistics: statistics(detail.map(sample => sample.totalMs)),
        perEndpointMedianMs: {
          scan: median(detail.map(sample => sample.scanMs)),
          inputs: median(detail.map(sample => sample.inputsMs)),
          guidelines: median(detail.map(sample => sample.guidelinesMs)),
          risk: median(detail.map(sample => sample.riskMs)),
          reviewRuns: median(detail.map(sample => sample.reviewRunsMs)),
          usage: median(detail.map(sample => sample.usageMs)),
        },
      },
    });
  }
  return {
    description: 'Warm /api/project, the QualityApi.selectRepository critical path (project+tree), '
      + 'and the deferred detail fan-out (scan, inputs, guidelines, risk, review runs, usage).',
    runs,
    targets: perTarget,
  };
}

async function measureReview(host, targets) {
  const perTarget = [];
  for (const target of targets) {
    const sensors = await timedJson(host, `/api/repos/${target.id}/sensors`);
    const sensorScans = [];
    for (const sensor of sensors.body?.sensors ?? []) {
      const scan = await timedJson(host, `/api/repos/${target.id}/sensors/${sensor.id}/scan`,
        { method: 'POST', timeoutMs: 600_000 });
      sensorScans.push({
        id: sensor.id,
        available: sensor.available ?? null,
        status: scan.status,
        totalMs: scan.totalMs,
        findings: Array.isArray(scan.body?.findings) ? scan.body.findings.length : null,
      });
    }

    // Review requests address a hierarchy node, so the subject is taken from the live tree.
    const tree = await timedJson(host, `/api/repos/${target.id}/tree?path=`);
    const subject = smallestModule(tree.body?.nodes ?? []);
    const estimates = [];
    for (const kind of ['code', 'security']) {
      const estimate = await timedJson(host, `/api/repos/${target.id}/review/estimate`, {
        method: 'POST',
        body: { path: subject?.path ?? '.', kind },
        timeoutMs: 900_000,
      });
      estimates.push({
        kind,
        subjectPath: subject?.path ?? null,
        status: estimate.status,
        totalMs: estimate.totalMs,
        body: estimate.body,
      });
    }

    const started = await timedJson(host, `/api/repos/${target.id}/review`, {
      method: 'POST',
      body: { path: subject?.path ?? '.', kind: 'code', confirmBelowFloor: true, force: true },
      timeoutMs: 900_000,
    });
    let run = null;
    let pollMs = null;
    if (started.status === 202 || started.status === 200) {
      const id = started.body?.id ?? started.body?.run?.id;
      const pollStarted = performance.now();
      for (let attempt = 0; attempt < 600; attempt += 1) {
        const poll = await timedJson(host, `/api/repos/${target.id}/review/runs/${id}`);
        run = poll.body;
        if (run && !['queued', 'running'].includes(run.state)) break;
        await delay(250);
      }
      pollMs = round(performance.now() - pollStarted);
      // Leave no run occupying the single-reader queue for the following measurements.
      if (run && ['queued', 'running'].includes(run.state)) {
        await timedJson(host, `/api/repos/${target.id}/review/runs/${id}`, { method: 'DELETE' });
      }
    }
    const report = run?.id
      ? await timedJson(host, `/api/repos/${target.id}/review/runs/${run.id}/report`)
      : null;

    perTarget.push({
      repositoryId: target.id,
      subject: subject ? { path: subject.path, level: subject.level, files: countFiles(subject) } : null,
      sensorList: { totalMs: sensors.totalMs, count: sensors.body?.sensors?.length ?? 0 },
      sensorScans,
      estimates,
      reviewStart: { status: started.status, totalMs: started.totalMs, body: started.body },
      reviewCompletion: { wallClockMs: pollMs, run },
      reviewReport: report ? { status: report.status, totalMs: report.totalMs } : null,
      relatedEvents: host.events.filter(event => typeof event.event === 'string'
        && event.event.startsWith('qs.review')).slice(-40),
    });
  }
  return {
    description: 'Deterministic sensor scans, review estimate, and a queued review run measured '
      + 'end to end with the phases the API itself reports.',
    targets: perTarget,
  };
}

async function measureMemory(host, targets, loops) {
  const samples = [];
  const sample = async (label, iteration) => samples.push({
    label,
    iteration,
    ...(await processMemory(host.child.pid)),
  });
  await sample('baseline', 0);
  for (let iteration = 1; iteration <= loops; iteration += 1) {
    for (const target of targets) {
      await Promise.all([
        timedJson(host, `/api/repos/${target.id}/project`),
        timedJson(host, `/api/repos/${target.id}/tree?path=`),
      ]);
      await Promise.all([
        timedJson(host, `/api/repos/${target.id}/scan`),
        timedJson(host, `/api/repos/${target.id}/inputs`),
        timedJson(host, `/api/repos/${target.id}/guidelines`),
        timedJson(host, `/api/repos/${target.id}/risk?days=90`),
        timedJson(host, `/api/repos/${target.id}/review/runs`),
        timedJson(host, `/api/repos/${target.id}/usage`),
      ]);
    }
    if (iteration % 5 === 0 || iteration === loops) await sample('session', iteration);
  }
  await delay(2_000);
  await sample('settled', loops);
  const first = samples.find(entry => entry.label === 'baseline');
  const last = samples[samples.length - 1];
  return {
    description: `Resident set size of the API process while ${loops} full repository-switch fan-outs `
      + 'are replayed per registered repository against one process.',
    loops,
    requestsPerLoop: targets.length * 8,
    samples,
    growth: {
      baselineRssMiB: first.rssMiB,
      finalRssMiB: last.rssMiB,
      deltaMiB: round(last.rssMiB - first.rssMiB),
      perLoopKiB: round(((last.rssMiB - first.rssMiB) * 1024) / loops),
    },
  };
}

// The smallest module keeps the queued review bounded while still exercising the real path.
function smallestModule(nodes) {
  const modules = [];
  const walk = list => {
    for (const node of list) {
      if (node.level === 'module') modules.push(node);
      if (node.children?.length) walk(node.children);
    }
  };
  walk(nodes);
  return modules.map(module => ({ ...module, files: countFiles(module) }))
    .filter(module => module.files > 0)
    .sort((left, right) => left.files - right.files)[0] ?? null;
}

function countFiles(node) {
  if (node.level === 'file') return 1;
  return (node.children ?? []).reduce((total, child) => total + countFiles(child), 0);
}

// `POST /api/review` and `/api/review/estimate` build the full prompt for every subject file
// synchronously inside the HTTP request. This records how that cost scales with subject size.
async function measureReviewScaling(host, targets) {
  const perTarget = [];
  for (const target of targets) {
    const tree = await timedJson(host, `/api/repos/${target.id}/tree?path=`);
    const modules = allModules(tree.body?.nodes ?? [])
      .map(module => ({ path: module.path, files: countFiles(module) }))
      .filter(module => module.files > 0)
      .sort((left, right) => left.files - right.files);
    // A spread of subject sizes from the smallest module to the largest.
    const picks = [...new Set([0, Math.floor(modules.length / 4), Math.floor(modules.length / 2),
      Math.floor((modules.length * 3) / 4), modules.length - 1].filter(index => index >= 0))]
      .map(index => modules[index]).filter(Boolean);
    const samples = [];
    for (const pick of picks) {
      const estimate = await timedJson(host, `/api/repos/${target.id}/review/estimate`, {
        method: 'POST',
        body: { path: pick.path, kind: 'code' },
        timeoutMs: 900_000,
      });
      samples.push({
        path: pick.path,
        files: pick.files,
        status: estimate.status,
        totalMs: estimate.totalMs,
        msPerFile: round(estimate.totalMs / pick.files),
        promptCharacters: estimate.body?.estimate?.promptCharacters ?? null,
      });
    }
    perTarget.push({
      repositoryId: target.id,
      moduleCount: modules.length,
      samples,
      // A transport-level failure means the host itself did not survive the request; keep its last words.
      apiTail: samples.some(sample => sample.status !== 200) ? host.lines.slice(-40) : undefined,
      apiExit: samples.some(sample => sample.status !== 200)
        ? { exitCode: host.child.exitCode, signal: host.child.signalCode }
        : undefined,
    });
  }
  return {
    description: 'POST /api/review/estimate against modules of increasing file count. The endpoint '
      + 'renders the full review prompt per file synchronously inside the request.',
    targets: perTarget,
  };
}

function allModules(nodes) {
  const modules = [];
  const walk = list => {
    for (const node of list) {
      if (node.level === 'module') modules.push(node);
      if (node.children?.length) walk(node.children);
    }
  };
  walk(nodes);
  return modules;
}

// A sensor scan persists evidence under `.quality/`, which is part of the repository's Git state.
// This records the dashboard cost of the request that follows a scan.
async function measureSensorInvalidation(host, targets) {
  const perTarget = [];
  for (const target of targets) {
    const warm = await timedJson(host, `/api/repos/${target.id}/project`);
    const scan = await timedJson(host, `/api/repos/${target.id}/sensors/coverage/scan`,
      { method: 'POST', timeoutMs: 600_000 });
    const after = await timedJson(host, `/api/repos/${target.id}/project`);
    const second = await timedJson(host, `/api/repos/${target.id}/project`);
    perTarget.push({
      repositoryId: target.id,
      warmBeforeScan: { totalMs: warm.totalMs, serverTiming: warm.serverTiming },
      scan: { id: 'coverage', status: scan.status, totalMs: scan.totalMs },
      firstRequestAfterScan: { totalMs: after.totalMs, serverTiming: after.serverTiming },
      secondRequestAfterScan: { totalMs: second.totalMs, serverTiming: second.serverTiming },
    });
  }
  return {
    description: 'Dashboard latency before a sensor scan, on the first request after it, and once '
      + 'the snapshot is warm again — the cost of a sensor writing into the repository Git state.',
    targets: perTarget,
  };
}

// Every accepted edit produces a new Git state, and the hierarchy/dashboard caches are keyed by it.
// This replays an editing session and records both the per-edit latency and the resident-set growth.
async function measureEditChurn(host, target, edits) {
  const scratch = join(target.path, 'src/quality/PerfChurnScratch.cs');
  const samples = [];
  const latencies = [];
  const before = await processMemory(host.child.pid);
  try {
    for (let edit = 1; edit <= edits; edit += 1) {
      await writeFile(scratch, `// QS-59 edit-churn scratch file, revision ${edit}\n`
        + `namespace Quality;\ninternal static class PerfChurnScratch${edit} { }\n`);
      const project = await timedJson(host, `/api/repos/${target.id}/project`);
      latencies.push({
        edit,
        totalMs: project.totalMs,
        serverTiming: project.serverTiming,
      });
      if (edit % 5 === 0 || edit === edits) {
        samples.push({ edit, ...(await processMemory(host.child.pid)) });
      }
    }
  } finally {
    await rm(scratch, { force: true });
  }
  const last = samples[samples.length - 1];
  return {
    description: 'One untracked-file edit per iteration followed by a repository-switch dashboard '
      + 'request, so every iteration produces a distinct Git state key.',
    repositoryId: target.id,
    trackedFiles: target.trackedFiles,
    edits,
    latency: {
      statistics: statistics(latencies.map(sample => sample.totalMs)),
      phaseMedianMs: {
        gitStatus: median(latencies.map(sample => sample.serverTiming?.['git-status'])),
        scan: median(latencies.map(sample => sample.serverTiming?.scan)),
        reviewMetaDiscovery: median(latencies.map(sample => sample.serverTiming?.['review-meta-discovery'])),
        projection: median(latencies.map(sample => sample.serverTiming?.projection)),
      },
      samples: latencies,
    },
    memory: {
      beforeRssMiB: before.rssMiB,
      afterRssMiB: last?.rssMiB ?? null,
      deltaMiB: last ? round(last.rssMiB - before.rssMiB) : null,
      perEditKiB: last ? round(((last.rssMiB - before.rssMiB) * 1024) / edits) : null,
      samples,
    },
  };
}

// ---------------------------------------------------------------- plumbing

async function startApi(targets, label) {
  const contentRoot = resolve(tempRoot, `host-${label}`);
  await mkdir(join(contentRoot, '.quality-studio'), { recursive: true });
  // The registry file is a plain array of RepositoryRegistration records.
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
  const events = [];
  const lines = [];
  const spawnedAt = performance.now();
  let firstLogMs = null;
  let listeningMs = null;
  const child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', contentRoot], {
    cwd: contentRoot,
    env: {
      ...process.env,
      DOTNET_ENVIRONMENT: 'Production',
      QualityStudio__RepositoryRoot: targets[0]?.path ?? seedRoot,
      QualityStudio__AllowedRoots__0: '/',
      QualityStudio__Security__Mode: 'Local',
      QualityStudio__Security__SpendRequestsPerMinute: '1000',
      Logging__LogLevel__Default: 'Information',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  children.push(child);
  const consume = stream => {
    let buffer = '';
    stream.setEncoding('utf8');
    stream.on('data', chunk => {
      buffer += chunk;
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        lines.push(line);
        if (firstLogMs === null) firstLogMs = round(performance.now() - spawnedAt);
        if (listeningMs === null && line.includes('Now listening on')) {
          listeningMs = round(performance.now() - spawnedAt);
        }
        const start = line.indexOf('{"event"');
        if (start >= 0) {
          try { events.push(JSON.parse(line.slice(start))); } catch { /* not a product event */ }
        }
      }
    });
  };
  consume(child.stdout);
  consume(child.stderr);

  const base = `http://127.0.0.1:${port}`;
  await waitForHttp(`${base}/health`, 60_000);
  const healthMs = round(performance.now() - spawnedAt);
  return { child, base, events, lines, healthMs, firstLogMs, listeningMs, contentRoot };
}

function cloneTarget(id, displayName, sourcePath) {
  if (!existsSync(join(sourcePath, '.git'))) throw new Error(`${sourcePath} is not a Git repository`);
  const path = resolve(tempRoot, `repo-${id}`);
  run('git', ['clone', '--quiet', '--no-hardlinks', sourcePath, path], true);
  return { id, displayName, path, sourcePath };
}

async function waitForPrewarm(host, ids, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const warmed = new Set(host.events
      .filter(event => event.event === 'qs.repository.prewarm')
      .map(event => event.repositoryId));
    if (ids.every(id => warmed.has(id))) return;
    await delay(200);
  }
  throw new Error(`prewarm did not complete for ${ids.join(', ')} within ${timeoutMs} ms`);
}

// node:http rather than fetch: several sensor scans run for minutes and undici's
// non-configurable 300 s headers timeout would abort the measurement instead of reporting it.
function timedJson(host, path, init = {}) {
  const payload = init.body ? JSON.stringify(init.body) : null;
  const started = performance.now();
  const url = new URL(`${host.base}${path}`);
  return new Promise(done => {
    const request = httpRequest({
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method: init.method ?? 'GET',
      headers: {
        accept: 'application/json',
        'X-Client-Id': 'qs-59-perf',
        ...(payload ? { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) } : {}),
      },
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        let body = null;
        try { body = JSON.parse(text); } catch { body = text.slice(0, 400); }
        done({
          path,
          status: response.statusCode,
          totalMs: round(performance.now() - started),
          bytes: Buffer.byteLength(text),
          serverTiming: parseServerTiming(response.headers['server-timing']),
          body,
        });
      });
    });
    request.setTimeout(init.timeoutMs ?? 900_000, () => {
      request.destroy(new Error(`request timeout after ${init.timeoutMs ?? 900_000} ms`));
    });
    request.on('error', error => done({
      path,
      status: 0,
      totalMs: round(performance.now() - started),
      bytes: 0,
      serverTiming: null,
      body: { error: error.message },
    }));
    if (payload) request.write(payload);
    request.end();
  });
}

function parseServerTiming(header) {
  if (!header) return null;
  const parsed = {};
  for (const part of header.split(',')) {
    const [name, ...rest] = part.trim().split(';');
    const duration = rest.map(entry => entry.trim()).find(entry => entry.startsWith('dur='));
    if (duration) parsed[name] = Number(duration.slice(4));
  }
  return parsed;
}

async function processMemory(pid) {
  const status = await readFile(`/proc/${pid}/status`, 'utf8');
  const value = name => {
    const match = status.match(new RegExp(`^${name}:\\s+(\\d+) kB`, 'm'));
    return match ? Number(match[1]) : null;
  };
  return {
    rssMiB: round(value('VmRSS') / 1024),
    peakRssMiB: round(value('VmHWM') / 1024),
    threads: Number(status.match(/^Threads:\s+(\d+)/m)?.[1] ?? 0),
  };
}

async function waitForHttp(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) { await response.text(); return; }
    } catch { /* the host is not listening yet */ }
    await delay(20);
  }
  throw new Error(`Timed out waiting for ${url}`);
}

function freePort() {
  return new Promise((resolvePort, reject) => {
    const server = createServer();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolvePort(port));
    });
  });
}

async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(done => child.once('exit', done)), delay(5_000)]);
  if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
}

function run(command, commandArguments, capture) {
  const result = spawnSync(command, commandArguments, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return capture ? (result.stdout ?? '') : result.status;
}

function cpuModel() {
  try {
    const info = readFileSync('/proc/cpuinfo', 'utf8');
    return info.match(/^model name\s*:\s*(.+)$/m)?.[1] ?? 'unknown';
  } catch { return 'unknown'; }
}

// -------------------------------------------------------------- statistics

function statistics(values) {
  const sorted = [...values].filter(Number.isFinite).sort((left, right) => left - right);
  if (sorted.length === 0) return null;
  return {
    count: sorted.length,
    minMs: round(sorted[0]),
    medianMs: median(sorted),
    p95Ms: round(sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1)]),
    maxMs: round(sorted[sorted.length - 1]),
  };
}

function median(values) {
  const sorted = [...values].filter(Number.isFinite).sort((left, right) => left - right);
  if (sorted.length === 0) return null;
  const middle = Math.floor(sorted.length / 2);
  return round(sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2);
}

function round(value) {
  return Number.isFinite(value) ? Math.round(value * 100) / 100 : null;
}

function pick(source, keys) {
  return Object.fromEntries(keys.map(key => [key, source[key]]));
}

function summarise(results) {
  return {
    startupMedianMs: {
      seededSingleRepository: results.startup?.statistics?.seededSingleRepository?.medianMs ?? null,
      allRegisteredRepositories: results.startup?.statistics?.allRegisteredRepositories?.medianMs ?? null,
    },
    switch: (results.switch?.targets ?? []).map(target => ({
      repositoryId: target.repositoryId,
      trackedFiles: target.trackedFiles,
      criticalPathMedianMs: target.criticalPath.statistics?.medianMs,
      deferredDetailMedianMs: target.deferredDetail.statistics?.medianMs,
      perEndpointMedianMs: target.deferredDetail.perEndpointMedianMs,
    })),
    memory: results.memory?.growth ?? results.memory,
    editChurn: results.editChurn?.latency
      ? { latency: results.editChurn.latency.statistics, memoryDeltaMiB: results.editChurn.memory.deltaMiB }
      : results.editChurn,
    review: (results.review?.targets ?? []).map(target => ({
      repositoryId: target.repositoryId,
      sensorListMs: target.sensorList.totalMs,
      sensorScanMs: Object.fromEntries(target.sensorScans.map(scan => [scan.id, scan.totalMs])),
      estimateMs: Object.fromEntries(target.estimates.map(estimate => [estimate.kind, estimate.totalMs])),
      reviewStartMs: target.reviewStart.totalMs,
      reviewCompletionMs: target.reviewCompletion.wallClockMs,
      reviewStatus: target.reviewCompletion.run?.status ?? null,
    })),
  };
}

function parseArguments(argv) {
  const parsed = {
    out: null, targets: [], stages: null,
    startupRuns: 5, switchRuns: 7, sessionLoops: 40, churnEdits: 40,
  };
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (flag === '--out') parsed.out = resolve(argv[++index]);
    else if (flag === '--startup-runs') parsed.startupRuns = Number(argv[++index]);
    else if (flag === '--switch-runs') parsed.switchRuns = Number(argv[++index]);
    else if (flag === '--session-loops') parsed.sessionLoops = Number(argv[++index]);
    else if (flag === '--churn-edits') parsed.churnEdits = Number(argv[++index]);
    else if (flag === '--stages') parsed.stages = argv[++index].split(',').map(entry => entry.trim());
    else if (flag === '--targets') {
      for (const entry of argv[++index].split(',')) {
        const [id, path] = entry.split('=');
        parsed.targets.push({ id, displayName: id, path: resolve(path) });
      }
    }
  }
  return parsed;
}
