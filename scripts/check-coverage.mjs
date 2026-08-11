import { readdir, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function parseCobertura(document) {
  const match = document.match(/<coverage\b[^>]*\bline-rate="([0-9.]+)"/i);
  if (!match) throw new Error('Cobertura report has no numeric coverage line-rate.');
  const lineRate = Number(match[1]);
  if (!Number.isFinite(lineRate) || lineRate < 0 || lineRate > 1) {
    throw new Error(`Cobertura line-rate is outside 0..1: ${match[1]}`);
  }
  return lineRate;
}

export function parseCoberturaPackages(document) {
  const packages = {};
  const pattern = /<package\b[^>]*\bname="([^"]+)"[^>]*\bline-rate="([0-9.]+)"/gi;
  for (const match of document.matchAll(pattern)) packages[match[1]] = validateRate(match[2], `Cobertura package ${match[1]}`);
  if (Object.keys(packages).length === 0) throw new Error('Cobertura report has no package line rates.');
  return packages;
}

export function parseLcov(document) {
  return parseLcovSources(document).lineRate;
}

export function parseLcovSources(document) {
  const lines = document.split(/\r?\n/);
  let found = 0;
  let hit = 0;
  const sources = {};
  let sourceName = null;
  let sourceFound = 0;
  let sourceHit = 0;
  for (const line of lines) {
    if (line.startsWith('SF:')) {
      sourceName = line.slice(3).replaceAll('\\', '/');
      sourceFound = 0;
      sourceHit = 0;
    } else if (line.startsWith('LF:')) {
      sourceFound = parseCount(line.slice(3), 'LF');
      found += sourceFound;
    } else if (line.startsWith('LH:')) {
      sourceHit = parseCount(line.slice(3), 'LH');
      hit += sourceHit;
    }
    else if (line === 'end_of_record' && sourceName) {
      sources[sourceName] = { found: sourceFound, hit: sourceHit };
      sourceName = null;
    }
  }
  if (found === 0) throw new Error('lcov report has no instrumented lines.');
  if (hit > found) throw new Error(`lcov hit lines (${hit}) exceed found lines (${found}).`);
  return { lineRate: hit / found, sources };
}

export function evaluateCoverage(baseline, measurements) {
  if (baseline.schemaVersion !== 1 || baseline.metric !== 'line-rate' || !baseline.projects) {
    throw new Error('Coverage baseline must use schemaVersion 1 and metric line-rate.');
  }
  const failures = [];
  const rows = [];
  for (const [name, expectation] of Object.entries(baseline.projects)) {
    const actual = measurements[name];
    if (actual === undefined) {
      failures.push(`${name}: report is missing`);
      continue;
    }
    const minimum = expectation.minimum;
    if (!Number.isFinite(minimum) || minimum < 0 || minimum > 1) {
      throw new Error(`${name}: baseline minimum must be between 0 and 1.`);
    }
    rows.push({ name, actual, minimum });
    if (actual + Number.EPSILON < minimum) {
      failures.push(`${name}: ${(actual * 100).toFixed(2)}% is below ${(minimum * 100).toFixed(2)}%`);
    }
    for (const [featureName, feature] of Object.entries(expectation.features ?? {})) {
      const measurementName = `${name}/${featureName}`;
      const featureActual = measurements[measurementName];
      if (featureActual === undefined) {
        failures.push(`${measurementName}: report is missing`);
        continue;
      }
      rows.push({ name: measurementName, actual: featureActual, minimum: feature.minimum });
      if (featureActual + Number.EPSILON < feature.minimum) {
        failures.push(`${measurementName}: ${(featureActual * 100).toFixed(2)}% is below ${(feature.minimum * 100).toFixed(2)}%`);
      }
    }
  }
  return { rows, failures };
}

async function main(arguments_) {
  const options = parseArguments(arguments_);
  const baseline = JSON.parse(await readFile(resolve(options.baseline), 'utf8'));
  const measurements = {};
  for (const report of options.cobertura) {
    const file = await findReport(report.path, 'coverage.cobertura.xml');
    const document = await readFile(file, 'utf8');
    measurements[report.name] = parseCobertura(document);
    const packages = parseCoberturaPackages(document);
    for (const [featureName, feature] of Object.entries(baseline.projects[report.name]?.features ?? {})) {
      const selected = feature.packages ?? [];
      if (selected.length !== 1 || packages[selected[0]] === undefined) {
        throw new Error(`${report.name}/${featureName}: expected one measured Cobertura package selector.`);
      }
      measurements[`${report.name}/${featureName}`] = packages[selected[0]];
    }
  }
  for (const report of options.lcov) {
    const file = await findReport(report.path, 'lcov.info');
    const parsed = parseLcovSources(await readFile(file, 'utf8'));
    measurements[report.name] = parsed.lineRate;
    for (const [featureName, feature] of Object.entries(baseline.projects[report.name]?.features ?? {})) {
      const prefixes = feature.sources ?? [];
      const selected = Object.entries(parsed.sources).filter(([source]) => prefixes.some(prefix => source.startsWith(prefix)));
      const found = selected.reduce((sum, [, value]) => sum + value.found, 0);
      const hit = selected.reduce((sum, [, value]) => sum + value.hit, 0);
      if (found === 0) throw new Error(`${report.name}/${featureName}: no instrumented source matched the baseline selectors.`);
      measurements[`${report.name}/${featureName}`] = hit / found;
    }
  }

  const result = evaluateCoverage(baseline, measurements);
  for (const row of result.rows) {
    console.log(`${row.name}: ${(row.actual * 100).toFixed(2)}% (minimum ${(row.minimum * 100).toFixed(2)}%)`);
  }
  if (result.failures.length) {
    for (const failure of result.failures) console.error(`coverage regression: ${failure}`);
    process.exitCode = 1;
  }
}

function parseArguments(arguments_) {
  const result = { baseline: '.quality/coverage-baseline.json', cobertura: [], lcov: [] };
  for (let index = 0; index < arguments_.length; index++) {
    const argument = arguments_[index];
    const value = arguments_[index + 1];
    if (argument === '--baseline' && value) {
      result.baseline = value;
      index++;
    } else if ((argument === '--cobertura' || argument === '--lcov') && value) {
      result[argument.slice(2)].push(parseNamedPath(value));
      index++;
    } else {
      throw new Error(`Unknown or incomplete coverage argument: ${argument}`);
    }
  }
  return result;
}

function parseNamedPath(value) {
  const separator = value.indexOf('=');
  if (separator < 1 || separator === value.length - 1) {
    throw new Error(`Coverage report must use name=path: ${value}`);
  }
  return { name: value.slice(0, separator), path: value.slice(separator + 1) };
}

async function findReport(path, expectedName) {
  const absolute = resolve(path);
  if (absolute.endsWith(expectedName)) return absolute;
  const matches = await walkFor(absolute, expectedName);
  if (matches.length !== 1) {
    throw new Error(`Expected exactly one ${expectedName} below ${absolute}; found ${matches.length}.`);
  }
  return matches[0];
}

async function walkFor(directory, expectedName) {
  const entries = await readdir(directory, { withFileTypes: true });
  const matches = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) matches.push(...await walkFor(path, expectedName));
    else if (entry.name === expectedName) matches.push(path);
  }
  return matches;
}

function parseCount(value, field) {
  const count = Number(value);
  if (!Number.isSafeInteger(count) || count < 0) throw new Error(`lcov ${field} value is invalid: ${value}`);
  return count;
}

function validateRate(value, label) {
  const rate = Number(value);
  if (!Number.isFinite(rate) || rate < 0 || rate > 1) throw new Error(`${label} line-rate is outside 0..1: ${value}`);
  return rate;
}

if (fileURLToPath(import.meta.url) === resolve(process.argv[1] ?? '')) {
  await main(process.argv.slice(2));
}
