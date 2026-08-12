import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptPath = fileURLToPath(import.meta.url);
const repositoryRoot = resolve(scriptPath, '..', '..');
const laneManifestPath = resolve(repositoryRoot, '.quality', 'test-lanes.json');
const npmCommand = process.platform === 'win32' ? 'npm.cmd' : 'npm';

export function portableFilter(laneManifest) {
  const categories = laneManifest?.portable?.excludedCategories;
  if (!Array.isArray(categories) || categories.length === 0 ||
      categories.some(category => typeof category !== 'string' || !/^[A-Za-z]+$/.test(category))) {
    throw new Error('portable.excludedCategories must be a non-empty array of category names');
  }
  return categories.map(category => `Category!=${category}`).join('&');
}

export function trxNotExecutedCount(document) {
  const counters = document.match(/<Counters\b[^>]*>/)?.[0];
  const value = counters?.match(/\bnotExecuted="(\d+)"/)?.[1];
  if (value === undefined) throw new Error('TRX result has no notExecuted counter');
  return Number(value);
}

async function main() {
  const startedAt = Date.now();
  const laneManifest = JSON.parse(await readFile(laneManifestPath, 'utf8'));
  const filter = portableFilter(laneManifest);
  const projects = laneManifest?.portable?.dotnetProjects;
  if (!Array.isArray(projects) || projects.length === 0) {
    throw new Error('portable.dotnetProjects must name the selected test projects');
  }

  const resultsRoot = await mkdtemp(join(tmpdir(), 'quality-studio-pre-review-'));
  try {
    await run(npmCommand, ['run', 'test:repository-contracts']);
    await run('dotnet', ['restore', 'QualityStudio.slnx', '--locked-mode']);

    for (const project of projects) {
      const resultName = `pre-review-${project.id}.trx`;
      await run('dotnet', [
        'test', project.path,
        '--configuration', 'Release',
        '--no-restore',
        '--filter', filter,
        '--logger', `trx;LogFileName=${resultName}`,
        '--results-directory', resultsRoot,
      ]);
      await requireNoSkippedTests(resolve(resultsRoot, resultName));
    }

    await run(npmCommand, ['--prefix', 'frontend', 'run', 'test:browser-resolver']);
    await run(npmCommand, ['--prefix', 'frontend', 'test']);
  } finally {
    await rm(resultsRoot, { recursive: true, force: true });
  }

  const durationSeconds = ((Date.now() - startedAt) / 1000).toFixed(1);
  console.log(`Pre-review portable assertions passed in ${durationSeconds}s with no retries or skipped selected tests.`);
  console.log('This fast target is not the required gate; production build, coverage, security, host integration, and release canary remain separate evidence.');
}

async function requireNoSkippedTests(resultPath) {
  const document = await readFile(resultPath, 'utf8');
  const notExecuted = trxNotExecutedCount(document);
  if (notExecuted !== 0) {
    throw new Error(`${basename(resultPath)} contains ${notExecuted} selected test(s) that did not execute`);
  }
}

async function run(command, args) {
  console.log(`\n> ${command} ${args.join(' ')}`);
  await new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, args, {
      cwd: repositoryRoot,
      env: process.env,
      stdio: 'inherit',
      shell: false,
      windowsHide: true,
    });
    child.once('error', rejectPromise);
    child.once('exit', (code, signal) => {
      if (code === 0) resolvePromise();
      else rejectPromise(new Error(`${command} failed with ${signal ? `signal ${signal}` : `exit code ${code}`}`));
    });
  });
}

if (process.argv[1] && resolve(process.argv[1]) === scriptPath) {
  main().catch(error => {
    console.error(`pre-review failed: ${error.message}`);
    process.exitCode = 1;
  });
}
