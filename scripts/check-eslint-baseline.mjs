#!/usr/bin/env node
// Gates on ESLint findings outside the recorded baseline (new debt only).
// Reuses the exact command the deterministic review sensor runs, so the CI gate
// and the model-review evidence never disagree on what ESLint reports.

import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(fileURLToPath(import.meta.url), '../..');
const baselinePath = path.join(repoRoot, '.quality/style/eslint.baseline.json');
const eslintBin = path.join(repoRoot, 'frontend/node_modules/eslint/bin/eslint.js');
const eslintConfig = path.join(repoRoot, 'frontend/eslint.config.mjs');

export function findingKey(file, finding) {
  return `${file}:${finding.ruleId}:${finding.line}`;
}

export function collectCurrentFindings({ runner = defaultRunner } = {}) {
  const raw = runner();
  return raw
    .filter((file) => file.messages.length > 0)
    .map((file) => ({
      file: path.relative(repoRoot, file.filePath).split(path.sep).join('/'),
      findings: file.messages.map((message) => ({
        ruleId: message.ruleId,
        line: message.line,
        column: message.column,
        severity: message.severity,
      })),
    }))
    .sort((a, b) => a.file.localeCompare(b.file));
}

function defaultRunner() {
  const result = spawnSync(
    process.execPath,
    [eslintBin, '.', '--config', eslintConfig, '--format', 'json'],
    { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 },
  );
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0 && result.status !== 1) {
    throw new Error(`eslint exited with ${result.status}: ${result.stderr}`);
  }
  return JSON.parse(result.stdout);
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
    console.log(`${resolved.length} baseline ESLint finding(s) no longer present (consider trimming the baseline):`);
    for (const item of resolved) {
      console.log(`  ${item.file}:${item.line} ${item.ruleId}`);
    }
  }

  if (newDrift.length > 0) {
    console.error(`${newDrift.length} new ESLint finding(s) outside the baseline:`);
    for (const item of newDrift) {
      console.error(`  ${item.file}:${item.line} ${item.ruleId}`);
    }
    console.error('\nFix the finding, or if it is pre-existing debt being intentionally captured, add it to .quality/style/eslint.baseline.json.');
    process.exitCode = 1;
    return;
  }

  console.log('No ESLint findings outside the baseline.');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main();
}
