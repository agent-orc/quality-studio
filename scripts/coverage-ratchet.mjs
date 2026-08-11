import { readdir, readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

export function parseCobertura(xml, source = 'coverage.cobertura.xml') {
  const coverage = xml.match(/<coverage\b[^>]*>/)?.[0];
  if (!coverage) throw new Error(`${source} does not contain a Cobertura coverage root.`);
  const covered = Number(attribute(coverage, 'lines-covered', source));
  const valid = Number(attribute(coverage, 'lines-valid', source));
  return measurement('cobertura', covered, valid, source);
}

export function parseLcov(lcov, source = 'lcov.info') {
  const covered = [...lcov.matchAll(/^LH:(\d+)$/gm)].reduce((sum, match) => sum + Number(match[1]), 0);
  const valid = [...lcov.matchAll(/^LF:(\d+)$/gm)].reduce((sum, match) => sum + Number(match[1]), 0);
  return measurement('lcov', covered, valid, source);
}

export function compareCoverage(baseline, current) {
  const failures = [];
  for (const [name, expected] of Object.entries(baseline.projects ?? {})) {
    const observed = current.projects[name];
    if (!observed) {
      failures.push(`${name}: coverage report is missing`);
      continue;
    }
    if (observed.rate + 1e-9 < expected.rate) {
      failures.push(`${name}: line coverage decreased from ${percent(expected.rate)} to ${percent(observed.rate)}`);
    }
  }
  for (const name of Object.keys(current.projects)) {
    if (!baseline.projects?.[name]) failures.push(`${name}: no committed baseline exists`);
  }
  return failures;
}

async function main() {
  const arguments_ = process.argv.slice(2);
  const writeBaseline = arguments_.includes('--write-baseline');
  const coverageRoot = resolve(repositoryRoot, option(arguments_, '--root') ?? 'artifacts/coverage');
  const frontendPath = resolve(repositoryRoot,
    option(arguments_, '--frontend') ?? 'frontend/coverage/frontend/lcov.info');
  const baselinePath = resolve(repositoryRoot,
    option(arguments_, '--baseline') ?? '.quality/coverage-baseline.json');

  const corePath = await findSingleCoverage(resolve(coverageRoot, 'dotnet-core'));
  const apiPath = await findSingleCoverage(resolve(coverageRoot, 'dotnet-api'));
  if (!existsSync(frontendPath)) throw new Error(`Angular lcov report is missing: ${frontendPath}`);
  const current = {
    schemaVersion: 1,
    projects: {
      'dotnet/core-tests': parseCobertura(await readFile(corePath, 'utf8'), relative(corePath)),
      'dotnet/api-tests': parseCobertura(await readFile(apiPath, 'utf8'), relative(apiPath)),
      'angular/frontend': parseLcov(await readFile(frontendPath, 'utf8'), relative(frontendPath)),
    },
  };

  if (writeBaseline) {
    await writeFile(baselinePath, `${JSON.stringify(current, null, 2)}\n`);
    console.log(`Wrote coverage baseline: ${relative(baselinePath)}`);
    return;
  }

  if (!existsSync(baselinePath)) throw new Error(`Coverage baseline is missing: ${baselinePath}`);
  const baseline = JSON.parse(await readFile(baselinePath, 'utf8'));
  if (baseline.schemaVersion !== 1) throw new Error(`Unsupported coverage baseline schema: ${baseline.schemaVersion}`);
  const failures = compareCoverage(baseline, current);
  if (failures.length) throw new Error(`Coverage ratchet failed:\n${failures.join('\n')}`);
  for (const [name, value] of Object.entries(current.projects))
    console.log(`${name}: ${percent(value.rate)} (${value.covered}/${value.valid} lines)`);
}

function attribute(element, name, source) {
  const value = element.match(new RegExp(`\\b${name}="([^"]+)"`))?.[1];
  if (value === undefined) throw new Error(`${source} is missing the Cobertura ${name} attribute.`);
  return value;
}

function measurement(format, covered, valid, source) {
  if (!Number.isInteger(covered) || !Number.isInteger(valid) || covered < 0 || valid <= 0 || covered > valid)
    throw new Error(`${source} contains invalid line totals: ${covered}/${valid}.`);
  return { format, covered, valid, rate: Number((covered / valid).toFixed(8)) };
}

function option(arguments_, name) {
  const index = arguments_.indexOf(name);
  if (index === -1) return null;
  if (!arguments_[index + 1]) throw new Error(`${name} requires a value.`);
  return arguments_[index + 1];
}

async function findSingleCoverage(root) {
  if (!existsSync(root)) throw new Error(`.NET coverage directory is missing: ${root}`);
  const matches = await findFiles(root, 'coverage.cobertura.xml');
  if (matches.length !== 1)
    throw new Error(`Expected one Cobertura report under ${root}, found ${matches.length}.`);
  return matches[0];
}

async function findFiles(root, name) {
  const matches = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = resolve(root, entry.name);
    if (entry.isDirectory()) matches.push(...await findFiles(path, name));
    else if (entry.name === name) matches.push(path);
  }
  return matches;
}

function relative(path) {
  return path.startsWith(repositoryRoot) ? path.slice(repositoryRoot.length + 1).replaceAll('\\', '/') : path;
}

function percent(rate) {
  return `${(rate * 100).toFixed(2)}%`;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}
