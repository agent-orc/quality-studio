import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const frontendRoot = join(repositoryRoot, 'frontend');
const PRETTIER_EXTENSIONS = new Set(['ts', 'html', 'css', 'mjs', 'cjs', 'json']);

// Prettier has never run against this tree, so a whole-file baseline would
// cover nearly every file (see docs/operations/static-analysis S3 note) and
// gate almost nothing. Instead, gate only files touched relative to the base
// branch: existing untouched drift stays visible but non-blocking, and any
// file a change actually touches must be Prettier-clean.
export function resolveChangedFiles({ diffOutput, statusOutput, watchedPrefix }) {
  const fromDiff = diffOutput
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
  const fromStatus = statusOutput
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.startsWith('??'))
    .map((line) => line.slice(2).trim());

  const files = new Set([...fromDiff, ...fromStatus]);
  return [...files]
    .filter((file) => file.startsWith(watchedPrefix))
    .filter((file) => PRETTIER_EXTENSIONS.has(file.split('.').pop()))
    .sort();
}

function git(args) {
  const result = spawnSync('git', args, { cwd: repositoryRoot, encoding: 'utf8' });
  if (result.status !== 0 && result.status !== 1) {
    throw new Error(`git ${args.join(' ')} failed: ${result.stderr}`);
  }
  return result.stdout ?? '';
}

async function main() {
  const baseRef = process.env.PRETTIER_BASE_REF || 'origin/main';
  const baseExists = spawnSync('git', ['rev-parse', '--verify', baseRef], {
    cwd: repositoryRoot,
    encoding: 'utf8',
  }).status === 0;

  if (!baseExists) {
    console.log(`Base ref "${baseRef}" not found locally; skipping changed-file format gate.`);
    return;
  }

  const diffOutput = git(['diff', '--name-only', '--diff-filter=ACMR', baseRef, '--', 'frontend/src', 'frontend/tests']);
  const statusOutput = git(['status', '--porcelain', '--', 'frontend/src', 'frontend/tests']);
  const changedRepoRelative = resolveChangedFiles({ diffOutput, statusOutput, watchedPrefix: 'frontend/' });

  if (changedRepoRelative.length === 0) {
    console.log(`No changed frontend/src or frontend/tests files relative to ${baseRef}; format gate has nothing to check.`);
    return;
  }

  const frontendRelative = changedRepoRelative.map((file) => file.slice('frontend/'.length));
  const result = spawnSync('npx', ['prettier', '--check', ...frontendRelative], {
    cwd: frontendRoot,
    encoding: 'utf8',
    stdio: 'inherit',
  });

  if (result.status !== 0) {
    console.error(`Prettier formatting is required for ${frontendRelative.length} file(s) changed relative to ${baseRef}.`);
    process.exitCode = 1;
    return;
  }

  console.log(`Prettier format gate passed for ${frontendRelative.length} changed file(s).`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await main();
}
