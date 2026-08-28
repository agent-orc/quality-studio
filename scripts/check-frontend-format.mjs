#!/usr/bin/env node
// Gates Prettier formatting on changed files only. Prettier has never run over this
// repository's full frontend history, so a whole-tree baseline would gate almost
// nothing (62 of ~64 files fail on first run) - checking only files that differ from
// the base ref keeps the gate meaningful without demanding a mass reformat up front.
//
// Base ref defaults to origin/main; override with PRETTIER_BASE_REF for local runs
// against a different comparison point.

import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(fileURLToPath(import.meta.url), '../..');
const prettierBin = path.join(repoRoot, 'frontend/node_modules/.bin/prettier');
const trackedDirs = ['frontend/src', 'frontend/tests'];

function git(args) {
  const result = spawnSync('git', args, { cwd: repoRoot, encoding: 'utf8' });
  if (result.status !== 0) {
    throw new Error(`git ${args.join(' ')} failed: ${result.stderr}`);
  }
  return result.stdout;
}

export function changedFiles(baseRef, { runner = git } = {}) {
  // Diffs the base ref against the working tree (not just HEAD), so this also
  // catches uncommitted changes during local/CI runs before a commit exists.
  const tracked = runner(['diff', '--name-only', baseRef])
    .split('\n')
    .filter(Boolean);
  const untracked = runner(['ls-files', '--others', '--exclude-standard'])
    .split('\n')
    .filter(Boolean);
  const all = new Set([...tracked, ...untracked]);
  return [...all]
    .filter((file) => trackedDirs.some((dir) => file.startsWith(`${dir}/`)))
    .filter((file) => /\.(ts|html|css|mjs|cjs|js)$/.test(file))
    .sort();
}

function main() {
  const baseRef = process.env.PRETTIER_BASE_REF ?? 'origin/main';
  let files;
  try {
    files = changedFiles(baseRef);
  } catch (error) {
    console.error(`Could not diff against base ref "${baseRef}": ${error.message}`);
    process.exitCode = 1;
    return;
  }

  if (files.length === 0) {
    console.log(`No changed frontend files vs ${baseRef}.`);
    return;
  }

  const result = spawnSync(prettierBin, ['--check', ...files], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  process.stdout.write(result.stdout);
  process.stderr.write(result.stderr);

  if (result.status !== 0) {
    console.error(`\n${files.length} changed file(s) checked vs ${baseRef}; formatting issues found above.`);
    console.error('Run "npx prettier --write <file>" (from frontend/) to fix.');
    process.exitCode = 1;
    return;
  }

  console.log(`${files.length} changed file(s) checked vs ${baseRef}; all formatted.`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main();
}
