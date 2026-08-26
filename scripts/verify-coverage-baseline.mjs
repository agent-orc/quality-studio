import { readFile, readdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
const options = parseArguments(process.argv.slice(2));
const baselinePath = resolve(options.baseline ?? join(repoRoot, '.quality', 'coverage', 'baseline.json'));
const dotnetRoot = resolve(options['dotnet-root'] ?? join(repoRoot, '.coverage', 'dotnet'));
const angularPath = resolve(options.angular ?? join(repoRoot, 'frontend', 'coverage', 'frontend', 'lcov.info'));

try {
  const baseline = JSON.parse(await readFile(baselinePath, 'utf8'));
  if (baseline.schemaVersion !== 1 || typeof baseline.projects !== 'object') {
    throw new Error(`Coverage baseline is not schema version 1: ${baselinePath}`);
  }

  const measurements = [];
  for (const [name, contract] of Object.entries(baseline.projects)) {
    const reportPath = contract.format === 'lcov'
      ? angularPath
      : await findSingleCobertura(join(dotnetRoot, contract.report));
    const measurement = contract.format === 'lcov'
      ? parseLcov(await readFile(reportPath, 'utf8'), reportPath)
      : parseCobertura(await readFile(reportPath, 'utf8'), reportPath);
    if (measurement.lineRate + Number.EPSILON < contract.minimumLineRate) {
      throw new Error(
        `${name} line coverage regressed: ${(measurement.lineRate * 100).toFixed(2)}% ` +
        `< ${(contract.minimumLineRate * 100).toFixed(2)}%`,
      );
    }
    measurements.push(`${name} ${(measurement.lineRate * 100).toFixed(2)}% (${measurement.covered}/${measurement.valid})`);
  }

  console.log(`coverage ratchet passed: ${measurements.join(' | ')}`);
} catch (error) {
  console.error(`coverage ratchet failed: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}

function parseArguments(values) {
  const parsed = {};
  for (let index = 0; index < values.length; index += 2) {
    const option = values[index];
    const value = values[index + 1];
    if (!option?.startsWith('--') || value === undefined) {
      throw new Error(`Expected --option value, received: ${values.slice(index).join(' ')}`);
    }
    parsed[option.slice(2)] = value;
  }
  return parsed;
}

async function findSingleCobertura(root) {
  const matches = await findFiles(root, 'coverage.cobertura.xml');
  if (matches.length !== 1) {
    throw new Error(`Expected one Cobertura report below ${root}, found ${matches.length}`);
  }
  return matches[0];
}

async function findFiles(root, fileName) {
  const matches = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) matches.push(...await findFiles(path, fileName));
    else if (entry.name === fileName) matches.push(path);
  }
  return matches;
}

function parseCobertura(xml, path) {
  const root = xml.match(/<coverage\b([^>]*)>/)?.[1];
  if (!root) throw new Error(`Unreadable Cobertura report: ${path}`);
  const covered = numericAttribute(root, 'lines-covered', path);
  const valid = numericAttribute(root, 'lines-valid', path);
  const declaredRate = numericAttribute(root, 'line-rate', path);
  if (valid <= 0 || covered < 0 || covered > valid || declaredRate < 0 || declaredRate > 1) {
    throw new Error(`Invalid Cobertura line totals in ${path}`);
  }
  const lineRate = covered / valid;
  if (Math.abs(lineRate - declaredRate) > 0.0001) {
    throw new Error(`Cobertura line-rate does not match its totals in ${path}`);
  }
  return { covered, valid, lineRate };
}

function numericAttribute(attributes, name, path) {
  const value = attributes.match(new RegExp(`\\b${name}="([^"]+)"`))?.[1];
  const parsed = Number(value);
  if (value === undefined || !Number.isFinite(parsed)) {
    throw new Error(`Cobertura ${name} is missing or invalid in ${path}`);
  }
  return parsed;
}

function parseLcov(content, path) {
  let covered = 0;
  let valid = 0;
  for (const line of content.split(/\r?\n/)) {
    if (line.startsWith('LH:')) covered += parseLcovNumber(line, path);
    if (line.startsWith('LF:')) valid += parseLcovNumber(line, path);
  }
  if (valid <= 0 || covered < 0 || covered > valid) {
    throw new Error(`Unreadable lcov line totals in ${path}`);
  }
  return { covered, valid, lineRate: covered / valid };
}

function parseLcovNumber(line, path) {
  const parsed = Number(line.slice(3));
  if (!Number.isInteger(parsed) || parsed < 0) throw new Error(`Invalid lcov counter in ${path}: ${line}`);
  return parsed;
}
