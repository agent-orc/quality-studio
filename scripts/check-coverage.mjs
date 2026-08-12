import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export function parseCobertura(document) {
  const coverage = document.match(/<coverage\b[^>]*\bline-rate="([0-9.]+)"/);
  if (!coverage) throw new Error('Cobertura report has no coverage line-rate.');
  return Number(coverage[1]);
}

export function parseLcov(document) {
  const found = [...document.matchAll(/^LF:(\d+)$/gm)].reduce((sum, match) => sum + Number(match[1]), 0);
  const hit = [...document.matchAll(/^LH:(\d+)$/gm)].reduce((sum, match) => sum + Number(match[1]), 0);
  if (found === 0 || hit > found) throw new Error('lcov report has invalid LF/LH totals.');
  return hit / found;
}

export function enforceBaseline(id, actual, minimum) {
  if (!Number.isFinite(actual) || !Number.isFinite(minimum)) {
    throw new Error(`${id} coverage is not a finite value.`);
  }
  if (actual + Number.EPSILON < minimum) {
    throw new Error(`${id} line coverage ${(actual * 100).toFixed(2)}% is below the committed ${(minimum * 100).toFixed(2)}% baseline.`);
  }
}

export async function findReport(root, fileName) {
  const matches = [];
  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      if (entry.isDirectory()) await visit(path);
      else if (entry.name === fileName) matches.push(path);
    }
  }
  try {
    await visit(resolve(root));
  } catch (error) {
    if (error?.code === 'ENOENT') throw new Error(`Coverage report directory is missing: ${root}`);
    throw error;
  }
  if (matches.length !== 1) {
    throw new Error(`Expected exactly one ${fileName} below ${root}, found ${matches.length}.`);
  }
  return matches[0];
}

async function main() {
  const baseline = JSON.parse(await readFile(resolve('coverage-baseline.json'), 'utf8'));
  if (baseline.version !== 1 || !Array.isArray(baseline.projects)) {
    throw new Error('coverage-baseline.json must contain version 1 and a projects array.');
  }

  for (const project of baseline.projects) {
    const report = await findReport(project.reportRoot, project.reportFile);
    const document = await readFile(report, 'utf8');
    const actual = project.format === 'cobertura' ? parseCobertura(document) : parseLcov(document);
    enforceBaseline(project.id, actual, project.minimumLineRate);
    console.log(`${project.id}: ${(actual * 100).toFixed(2)}% (baseline ${(project.minimumLineRate * 100).toFixed(2)}%)`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}
