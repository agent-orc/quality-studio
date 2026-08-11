import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { compareObservations } from './static-analysis-baseline-core.mjs';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDir, '..');
const frontendRoot = join(repositoryRoot, 'frontend');
const baselinePath = join(repositoryRoot, '.quality', 'static-analysis', 'style-baseline.json');
const mode = process.argv[2];
const onlyIndex = process.argv.indexOf('--only');
const selected = onlyIndex >= 0 ? new Set(process.argv[onlyIndex + 1].split(',')) : null;
const supported = new Set(['dotnet', 'eslint', 'prettier']);

if (!['capture', 'check'].includes(mode)) {
  fail('Usage: node scripts/static-analysis-baseline.mjs <capture|check> [--only dotnet,eslint,prettier]');
}
if (selected && [...selected].some((tool) => !supported.has(tool))) {
  fail(`Unknown tool in --only: ${[...selected].filter((tool) => !supported.has(tool)).join(', ')}`);
}

const include = (tool) => !selected || selected.has(tool);
const observations = [
  ...(include('dotnet') ? collectDotNet() : []),
  ...(include('eslint') ? collectEslint() : []),
  ...(include('prettier') ? collectPrettier() : []),
].sort(compareObservation);

if (mode === 'capture') {
  if (selected) fail('A baseline capture must include every configured tool.');
  mkdirSync(dirname(baselinePath), { recursive: true });
  writeFileSync(
    baselinePath,
    `${JSON.stringify({ schemaVersion: 1, observations }, null, 2)}\n`,
    'utf8',
  );
  console.log(`Captured ${observations.length} static-analysis baseline observation(s).`);
  process.exit(0);
}

let baseline;
try {
  baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
} catch (error) {
  fail(`Static-analysis baseline is unavailable: ${error.message}`);
}
if (baseline.schemaVersion !== 1 || !Array.isArray(baseline.observations)) {
  fail('Static-analysis baseline must use schemaVersion 1 and contain observations.');
}
const comparison = compareObservations(baseline.observations, observations, selected);
const { added, resolved } = comparison;
console.log(
  `Static baseline: ${comparison.current.length} current, ${comparison.existing.length} existing, ${added.length} new, ${resolved.length} resolved.`,
);
for (const observation of added) {
  console.error(`NEW ${observation.tool} ${observation.path}:${observation.line}:${observation.column} ${observation.rule}`);
}
for (const observation of resolved) {
  console.log(`RESOLVED ${observation.tool} ${observation.path}:${observation.line}:${observation.column} ${observation.rule}`);
}
process.exit(added.length === 0 ? 0 : 1);

function collectDotNet() {
  const reportDirectory = mkdtempSync(join(tmpdir(), 'quality-studio-format-'));
  try {
    run(
      'dotnet',
      [
        'format',
        'QualityStudio.slnx',
        'whitespace',
        '--verify-no-changes',
        '--no-restore',
        '--report',
        reportDirectory,
        '--verbosity',
        'quiet',
      ],
      repositoryRoot,
      new Set([0, 2]),
    );
    const report = JSON.parse(readFileSync(join(reportDirectory, 'format-report.json'), 'utf8'));
    return report.flatMap((document) =>
      document.FileChanges.map((change) => observation(
        'dotnet',
        normalizedPath(document.FilePath),
        change.LineNumber,
        change.CharNumber,
        change.DiagnosticId,
        change.FormatDescription,
      )),
    );
  } finally {
    rmSync(reportDirectory, { recursive: true, force: true });
  }
}

function collectEslint() {
  const eslint = join(frontendRoot, 'node_modules', 'eslint', 'bin', 'eslint.js');
  const result = run(
    process.execPath,
    [eslint, '.', '--config', 'frontend/eslint.config.mjs', '--format', 'json', '--no-color'],
    repositoryRoot,
    new Set([0, 1]),
  );
  let report;
  try {
    report = JSON.parse(result.stdout);
  } catch (error) {
    fail(`ESLint did not produce parseable JSON: ${error.message}\n${result.stderr}`);
  }
  return report.flatMap((file) => file.messages.map((message) => observation(
    'eslint',
    normalizedPath(file.filePath),
    message.line ?? 1,
    message.column ?? 1,
    message.ruleId ?? 'eslint-fatal',
    message.message,
  )));
}

function collectPrettier() {
  const prettier = join(frontendRoot, 'node_modules', 'prettier', 'bin', 'prettier.cjs');
  const result = run(
    process.execPath,
    [
      prettier,
      '--list-different',
      '--no-color',
      'src/**/*.{ts,html,css,scss}',
      'tests/**/*.{mjs,cjs}',
      '*.{json,ts,mjs,cjs}',
    ],
    frontendRoot,
    new Set([0, 1]),
  );
  return result.stdout.split(/\r?\n/u).filter(Boolean).map((path) =>
    observation('prettier', normalizedPath(resolve(frontendRoot, path)), 1, 1, 'prettier', 'File differs from Prettier output.'));
}

function observation(tool, path, line, column, rule, message) {
  const material = `${tool}\0${path}\0${line}\0${column}\0${rule}\0${message}`;
  return {
    tool,
    path,
    line,
    column,
    rule,
    fingerprint: `sha256:${createHash('sha256').update(material).digest('hex')}`,
  };
}

function normalizedPath(path) {
  return relative(repositoryRoot, resolve(path)).replaceAll('\\', '/');
}

function compareObservation(left, right) {
  return left.tool.localeCompare(right.tool) ||
    left.path.localeCompare(right.path) ||
    left.line - right.line ||
    left.column - right.column ||
    left.rule.localeCompare(right.rule);
}

function run(executable, args, cwd, accepted) {
  const result = spawnSync(executable, args, {
    cwd,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, NO_COLOR: '1' },
  });
  if (result.error) fail(`${executable} could not start: ${result.error.message}`);
  if (!accepted.has(result.status)) {
    fail(`${executable} exited with code ${result.status}.\n${result.stdout}\n${result.stderr}`);
  }
  return result;
}

function fail(message) {
  console.error(message);
  process.exit(2);
}
