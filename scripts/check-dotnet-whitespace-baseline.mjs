#!/usr/bin/env node
// Gates on .NET whitespace formatting drift outside the recorded baseline.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(fileURLToPath(import.meta.url), '../..');
const baselinePath = path.join(repoRoot, '.quality/style/dotnet-whitespace.baseline.json');
const solution = path.join(repoRoot, 'QualityStudio.slnx');

export function findingKey(file, finding) {
  return `${file}:${finding.line}:${finding.char}:${finding.description}`;
}

export function collectCurrentFindings({ runner = defaultRunner } = {}) {
  const reportDir = mkdtempSync(path.join(tmpdir(), 'dotnet-whitespace-'));
  try {
    runner(reportDir);
    const reportPath = path.join(reportDir, 'format-report.json');
    const raw = JSON.parse(readFileSync(reportPath, 'utf8'));
    return raw
      .map((entry) => ({
        file: path.relative(repoRoot, entry.FilePath).split(path.sep).join('/'),
        findings: entry.FileChanges.map((change) => ({
          line: change.LineNumber,
          char: change.CharNumber,
          description: change.FormatDescription,
        })),
      }))
      .sort((a, b) => a.file.localeCompare(b.file));
  } finally {
    rmSync(reportDir, { recursive: true, force: true });
  }
}

function defaultRunner(reportDir) {
  spawnSync(
    'dotnet',
    ['format', 'whitespace', solution, '--verify-no-changes', '--report', reportDir],
    { cwd: repoRoot, stdio: 'ignore' },
  );
}

export function diffAgainstBaseline(current, baseline) {
  const baselineSet = new Set();
  for (const entry of baseline.entries) {
    for (const finding of entry.findings) {
      baselineSet.add(findingKey(entry.file, finding));
    }
  }

  const currentSet = new Set();
  const newDrift = [];
  for (const entry of current) {
    for (const finding of entry.findings) {
      const key = findingKey(entry.file, finding);
      currentSet.add(key);
      if (!baselineSet.has(key)) {
        newDrift.push({ file: entry.file, ...finding });
      }
    }
  }

  const resolved = [];
  for (const entry of baseline.entries) {
    for (const finding of entry.findings) {
      const key = findingKey(entry.file, finding);
      if (!currentSet.has(key)) {
        resolved.push({ file: entry.file, ...finding });
      }
    }
  }

  return { newDrift, resolved };
}

async function main() {
  const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
  const current = collectCurrentFindings();
  const { newDrift, resolved } = diffAgainstBaseline(current, baseline);

  if (resolved.length > 0) {
    console.log(`${resolved.length} baseline whitespace finding(s) no longer present (consider trimming the baseline):`);
    for (const item of resolved) {
      console.log(`  ${item.file}:${item.line} ${item.description}`);
    }
  }

  if (newDrift.length > 0) {
    console.error(`${newDrift.length} new .NET whitespace finding(s) outside the baseline:`);
    for (const item of newDrift) {
      console.error(`  ${item.file}:${item.line} ${item.description}`);
    }
    console.error('\nRun "dotnet format whitespace QualityStudio.slnx" to fix, or update .quality/style/dotnet-whitespace.baseline.json if this is pre-existing debt.');
    process.exitCode = 1;
    return;
  }

  console.log('No .NET whitespace drift outside the baseline.');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main();
}
