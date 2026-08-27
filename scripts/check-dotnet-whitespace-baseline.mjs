import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const baselinePath = join(repositoryRoot, '.quality', 'style', 'dotnet-whitespace.baseline.json');
const solutionPath = join(repositoryRoot, 'QualityStudio.slnx');

const WHITESPACE_ERROR_PATTERN = /^(.*?)\(\d+,\d+\): error WHITESPACE:/;

export function parseDriftedFiles(dotnetFormatOutput, repositoryRootPath) {
  const files = new Set();
  for (const line of dotnetFormatOutput.split(/\r?\n/)) {
    const match = WHITESPACE_ERROR_PATTERN.exec(line.trim());
    if (!match) continue;
    const absolutePath = match[1].trim();
    const relativePath = resolve(absolutePath).startsWith(repositoryRootPath)
      ? resolve(absolutePath).slice(repositoryRootPath.length + 1).replaceAll('\\', '/')
      : absolutePath.replaceAll('\\', '/');
    files.add(relativePath);
  }
  return [...files].sort();
}

export function classifyDrift(driftedFiles, baselineFiles) {
  const drifted = new Set(driftedFiles);
  const baseline = new Set(baselineFiles);
  return {
    newDrift: driftedFiles.filter((file) => !baseline.has(file)).sort(),
    existing: driftedFiles.filter((file) => baseline.has(file)).sort(),
    resolved: baselineFiles.filter((file) => !drifted.has(file)).sort(),
  };
}

async function main() {
  const baseline = JSON.parse(await readFile(baselinePath, 'utf8'));
  const result = spawnSync('dotnet', ['format', 'whitespace', '--verify-no-changes', solutionPath], {
    encoding: 'utf8',
  });
  const output = `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
  const drifted = parseDriftedFiles(output, repositoryRoot);
  const { newDrift, existing, resolved } = classifyDrift(drifted, baseline.files);

  if (resolved.length > 0) {
    console.log(`Resolved baseline entries (remove from ${baselinePath}):`);
    for (const file of resolved) console.log(`  ${file}`);
  }
  if (existing.length > 0) {
    console.log(`Existing whitespace debt (baselined, not failing): ${existing.length} file(s).`);
  }

  if (newDrift.length > 0) {
    console.error('New .NET whitespace drift outside the baseline:');
    for (const file of newDrift) console.error(`  ${file}`);
    console.error(`Run "dotnet format whitespace" to fix, or add to ${baselinePath} if intentional.`);
    process.exitCode = 1;
    return;
  }

  if (result.status !== 0 && drifted.length === 0) {
    console.error('dotnet format whitespace failed without reporting parseable WHITESPACE findings:');
    console.error(output.trim());
    process.exitCode = result.status ?? 1;
    return;
  }

  console.log('.NET whitespace gate passed: no drift outside the baseline.');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await main();
}
