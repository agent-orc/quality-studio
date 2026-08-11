import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const apiDll = process.env.QS_API_DLL || resolve(repositoryRoot, 'src/QualityStudio.Api/bin/Release/net10.0/QualityStudio.Api.dll');
const resultsRoot = process.env.JOB_RESULTS_DIR || resolve(repositoryRoot, 'results');
const largeRepository = process.env.QS_LARGE_REPOSITORY || '/home/agent/runner-work/PROJ-002/repo';
const state = { children: [], tempRoots: [] };

try {
  await mkdir(resultsRoot, { recursive: true });
  const environment = {
    measuredAt: new Date().toISOString(),
    node: process.version,
    dotnet: command('dotnet', ['--version']),
    kernel: command('uname', ['-sr']),
    cpu: command('bash', ['-lc', "lscpu | sed -n 's/^Model name:[[:space:]]*//p'"]),
    logicalCpus: Number(command('nproc', [])),
    commit: command('git', ['-C', repositoryRoot, 'rev-parse', 'HEAD']),
  };

  const startup = {};
  startup.qualityStudio = await startupSeries('quality-studio', repositoryRoot, 5);
  startup.agentTaskboard = await startupSeries('agent-taskboard', largeRepository, 3);

  const largeRepo = await measureWarmRepository('agent-taskboard', largeRepository, 20);
  const memory = await measureLongSessionMemory();
  const result = { environment, startup, largeRepo, memory };
  await writeFile(resolve(resultsRoot, 'performance-benchmark.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} finally {
  await Promise.allSettled(state.children.map(stop));
  await Promise.allSettled(state.tempRoots.map(root => rm(root, { recursive: true, force: true })));
}

async function startupSeries(id, root, repetitions) {
  const samples = [];
  for (let iteration = 0; iteration < repetitions; iteration++) {
    const host = await startApi([{ id: 'default', displayName: id, rootPath: root }]);
    const usableStarted = performance.now();
    const responses = await Promise.all([
      timedFetch(`${host.url}/api/project`),
      timedFetch(`${host.url}/api/tree?path=`),
    ]);
    const usableMs = performance.now() - usableStarted;
    await waitFor(() => host.lines.some(line => line.includes('"event":"qs.repository.prewarm"')), 60_000, `${id} prewarm`);
    const prewarm = parseEvent(host.lines, 'qs.repository.prewarm', 'default');
    samples.push({
      iteration: iteration + 1,
      processToHealthMs: round(host.healthMs),
      healthToProjectAndTreeMs: round(usableMs),
      processToProjectAndTreeMs: round(host.healthMs + usableMs),
      projectClientMs: round(responses[0].durationMs),
      treeClientMs: round(responses[1].durationMs),
      projectServerTiming: responses[0].serverTiming,
      treeServerTiming: responses[1].serverTiming,
      rssAfterUsableKiB: await rssKiB(host.child.pid),
      prewarm,
    });
    await stop(host.child);
  }
  return { root, trackedFiles: trackedFiles(root), samples, summary: summarizeStartup(samples) };
}

async function measureWarmRepository(id, root, repetitions) {
  const host = await startApi([{ id: 'default', displayName: id, rootPath: root }]);
  await waitFor(() => host.lines.some(line => line.includes('"event":"qs.repository.prewarm"')), 60_000, `${id} prewarm`);
  const prewarm = parseEvent(host.lines, 'qs.repository.prewarm', 'default');
  const samples = [];
  for (let iteration = 0; iteration < repetitions; iteration++) {
    const started = performance.now();
    const [project, tree] = await Promise.all([
      timedFetch(`${host.url}/api/project`),
      timedFetch(`${host.url}/api/tree?path=`),
    ]);
    samples.push({
      iteration: iteration + 1,
      projectAndTreeMs: round(performance.now() - started),
      projectMs: round(project.durationMs),
      treeMs: round(tree.durationMs),
      projectBytes: project.bytes,
      treeBytes: tree.bytes,
      projectServerTiming: project.serverTiming,
      treeServerTiming: tree.serverTiming,
    });
  }
  const rss = await rssKiB(host.child.pid);
  await stop(host.child);
  return {
    root,
    commit: command('git', ['-C', root, 'rev-parse', 'HEAD']),
    trackedFiles: trackedFiles(root),
    prewarm,
    rssAfterWarmKiB: rss,
    samples,
    summary: summarize(samples.map(sample => sample.projectAndTreeMs)),
  };
}

async function measureLongSessionMemory() {
  const root = await mkdtemp(resolve(tmpdir(), 'qs-memory-session-'));
  state.tempRoots.push(root);
  await createFixtureRepository(root, 1_600);
  const host = await startApi([{ id: 'default', displayName: 'memory fixture', rootPath: root }]);
  await waitFor(() => host.lines.some(line => line.includes('"event":"qs.repository.prewarm"')), 60_000, 'memory fixture prewarm');

  // Warm the invalidation path before taking the baseline so JIT growth is not
  // mistaken for retained repository projections.
  for (let iteration = 0; iteration < 5; iteration++) {
    await writeFile(resolve(root, 'session-state.txt'), `warm-${iteration}\n`);
    await timedFetch(`${host.url}/api/project`);
  }
  await delay(250);
  const samples = [{ invalidations: 0, ...(await processMemory(host.child.pid)) }];
  const durations = [];
  for (let iteration = 1; iteration <= 100; iteration++) {
    await writeFile(resolve(root, 'session-state.txt'), `state-${iteration}-${'x'.repeat(iteration % 97)}\n`);
    const response = await timedFetch(`${host.url}/api/project`);
    durations.push(response.durationMs);
    if (iteration % 10 === 0) samples.push({ invalidations: iteration, ...(await processMemory(host.child.pid)) });
  }
  const final = samples.at(-1);
  const baseline = samples[0];
  await stop(host.child);
  return {
    fixture: 'generated Git repository with 1,603 tracked Angular/TypeScript files',
    mechanism: '100 distinct dirty Git states; GET /api/project after each invalidation',
    samples,
    retainedGrowthKiB: final.rssAnonKiB - baseline.rssAnonKiB,
    retainedGrowthPercent: round((final.rssAnonKiB - baseline.rssAnonKiB) * 100 / baseline.rssAnonKiB),
    requestSummary: summarize(durations),
  };
}

async function startApi(registrations) {
  const contentRoot = await mkdtemp(resolve(tmpdir(), 'qs-api-host-'));
  state.tempRoots.push(contentRoot);
  const registryDirectory = resolve(contentRoot, '.quality-studio');
  await mkdir(registryDirectory, { recursive: true });
  await writeFile(resolve(registryDirectory, 'repositories.json'), JSON.stringify(registrations.map(repository => ({
    ...repository,
    globalInputsDirectory: null,
    inputBudgetCharacters: 12_000,
    enabledReviewKinds: ['code', 'security', 'performance'],
    sensors: null,
    archived: false,
    defaultReviewTokenCap: 100_000,
    defaultReviewCostCap: null,
  })), null, 2));
  const port = await freePort();
  const lines = [];
  const started = performance.now();
  const child = spawn('dotnet', [apiDll, '--urls', `http://127.0.0.1:${port}`, '--contentRoot', contentRoot], {
    cwd: contentRoot,
    env: {
      ...process.env,
      QualityStudio__RepositoryRoot: registrations[0].rootPath,
      QualityStudio__AllowedRoots__0: dirname(registrations[0].rootPath),
      QualityStudio__Security__Mode: 'Local',
      DOTNET_GCConserveMemory: '0',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  state.children.push(child);
  for (const stream of [child.stdout, child.stderr]) collectLines(stream, lines);
  await waitForHttp(`http://127.0.0.1:${port}/health`, 30_000);
  return { child, lines, url: `http://127.0.0.1:${port}`, healthMs: performance.now() - started };
}

async function timedFetch(url) {
  const started = performance.now();
  const response = await fetch(url);
  const body = await response.arrayBuffer();
  if (!response.ok) throw new Error(`${url} returned ${response.status}`);
  return {
    durationMs: performance.now() - started,
    bytes: body.byteLength,
    serverTiming: response.headers.get('server-timing'),
  };
}

function parseEvent(lines, event, repositoryId) {
  const line = [...lines].reverse().find(candidate => candidate.includes(`"event":"${event}"`) && candidate.includes(`"repositoryId":"${repositoryId}"`));
  if (!line) return null;
  const start = line.indexOf('{"event"');
  if (start < 0) return { raw: line };
  try { return JSON.parse(line.slice(start)); } catch { return { raw: line }; }
}

function summarizeStartup(samples) {
  return {
    processToHealthMs: summarize(samples.map(sample => sample.processToHealthMs)),
    processToProjectAndTreeMs: summarize(samples.map(sample => sample.processToProjectAndTreeMs)),
  };
}

function summarize(values) {
  const sorted = [...values].sort((left, right) => left - right);
  return {
    count: sorted.length,
    min: round(sorted[0]),
    median: round(percentile(sorted, 0.5)),
    p95: round(percentile(sorted, 0.95)),
    max: round(sorted.at(-1)),
  };
}

function percentile(sorted, quantile) {
  const index = (sorted.length - 1) * quantile;
  const lower = Math.floor(index);
  const upper = Math.ceil(index);
  return lower === upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
}

async function createFixtureRepository(root, fileCount) {
  await mkdir(resolve(root, 'src'), { recursive: true });
  await writeFile(resolve(root, 'angular.json'), JSON.stringify({ projects: { fixture: { root: '', sourceRoot: 'src' } } }));
  await writeFile(resolve(root, 'package.json'), JSON.stringify({ name: 'quality-studio-memory-fixture', private: true }));
  await writeFile(resolve(root, '.gitignore'), 'node_modules\ndist\ncoverage\n');
  for (let start = 0; start < fileCount; start += 100) {
    await Promise.all(Array.from({ length: Math.min(100, fileCount - start) }, async (_, offset) => {
      const index = start + offset;
      const feature = `feature-${String(index % 40).padStart(2, '0')}`;
      const directory = resolve(root, 'src', feature);
      await mkdir(directory, { recursive: true });
      await writeFile(resolve(directory, `repository-service-${String(index).padStart(4, '0')}.ts`), [
        `export interface Record${index} { id: string; updatedAt: string; }`,
        `export class RepositoryService${index} {`,
        `  readonly feature = '${feature}';`,
        `  select(records: Record${index}[], id: string) { return records.find(record => record.id === id); }`,
        '}',
        '',
      ].join('\n'));
    }));
  }
  runGit(root, 'init', '--quiet');
  runGit(root, 'config', 'user.email', 'perf-harness@example.invalid');
  runGit(root, 'config', 'user.name', 'Quality Studio Perf Harness');
  runGit(root, 'add', '.');
  runGit(root, 'commit', '--quiet', '-m', 'Create long-session fixture');
}

function runGit(root, ...arguments_) {
  const result = spawnSync('git', ['-C', root, ...arguments_], { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`git ${arguments_.join(' ')} failed: ${result.stderr}`);
}

function trackedFiles(root) {
  return Number(command('bash', ['-lc', `git -C "$1" ls-files -z | tr -cd '\\0' | wc -c`, 'bash', root]));
}

function command(executable, arguments_) {
  const result = spawnSync(executable, arguments_, { encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`${executable} failed: ${result.stderr}`);
  return result.stdout.trim();
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

async function processMemory(pid) {
  const status = await readFile(`/proc/${pid}/status`, 'utf8');
  const value = name => Number(status.match(new RegExp(`^${name}:\\s+(\\d+) kB$`, 'm'))?.[1] ?? 0);
  return { vmRssKiB: value('VmRSS'), rssAnonKiB: value('RssAnon'), rssFileKiB: value('RssFile') };
}

async function rssKiB(pid) {
  return (await processMemory(pid)).vmRssKiB;
}

async function freePort() {
  const server = createServer();
  await new Promise((resolvePromise, rejectPromise) => server.listen(0, '127.0.0.1', resolvePromise).once('error', rejectPromise));
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
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
    await delay(10);
  }
  throw new Error(`Timed out waiting for ${label}`);
}

async function stop(child) {
  if (child.exitCode !== null) return;
  child.kill('SIGTERM');
  await Promise.race([new Promise(resolvePromise => child.once('exit', resolvePromise)), delay(5_000)]);
  if (child.exitCode === null) child.kill('SIGKILL');
}

function round(value) {
  return Number(value.toFixed(2));
}
