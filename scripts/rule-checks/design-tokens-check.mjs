#!/usr/bin/env node
// Deterministic pre-check for QS-NG-003 / QS-NG-004 (rules/angular). Scans CSS for raw hex colors
// and raw px literals design tokens already cover, and for the same raw literal repeated across two
// or more stylesheets. Emits a SARIF 2.1.0 report (schemas/sarif-2.1.0-output.schema.json) that
// SarifSensor (src/AgentOrchestrator.CodeQuality/SarifSensor.cs) ingests as-is -- no runtime code
// change is needed to plug this into the deterministic evidence a review prompt sees. See
// docs/concepts/rule-library.md#deterministic-pre-checks for how to register it as a sensor.
//
// Usage: node scripts/rule-checks/design-tokens-check.mjs [targetDir] [reportPath]
//   targetDir  defaults to frontend/src
//   reportPath defaults to .quality/rule-checks/design-tokens.sarif.json

import { readFile, writeFile, mkdir, readdir, stat } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const targetDir = resolve(repositoryRoot, process.argv[2] ?? 'frontend/src/app');
const reportPath = resolve(repositoryRoot, process.argv[3] ?? '.quality/rule-checks/design-tokens.sarif.json');
const tokenSourcePath = resolve(repositoryRoot, 'frontend/src/styles.css');

const HEX_COLOR = /#[0-9a-fA-F]{3,8}\b/g;
// Raw px values, excluding the accepted 1px hairline-border exception (QS-NG-003).
const RAW_PX = /(?<![\w-])(?!1px\b)\d+(?:\.\d+)?px\b/g;

async function collectCssFiles(dir) {
  const metadata = await stat(dir);
  if (metadata.isFile()) return dir.endsWith('.css') ? [dir] : [];
  const entries = await readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    if (entry.name === 'node_modules' || entry.name.startsWith('.')) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) files.push(...(await collectCssFiles(full)));
    else if (entry.name.endsWith('.css')) files.push(full);
  }
  return files;
}

function stripRootBlocks(content) {
  // Token declarations themselves (":root { --studio-space-1: 4px; ... }") are the source of
  // truth, not a violation; strip every :root[...]{...} block before scanning for raw literals.
  return content
    .replace(/:root(?:\[[^\]]*])?\s*\{[^}]*\}/g, (match) => ' '.repeat(match.length))
    .replace(/\/\*[\s\S]*?\*\//g, (match) => match.replace(/[^\n]/g, ' '));
}

function tokenLiterals(content) {
  const values = new Map();
  const declaration = /(--[a-zA-Z0-9-]+)\s*:\s*(#[0-9a-fA-F]{3,8}\b|\d+(?:\.\d+)?px\b)/g;
  for (const match of content.matchAll(declaration)) {
    const literal = match[2].toLowerCase();
    const names = values.get(literal) ?? new Set();
    names.add(match[1]);
    values.set(literal, names);
  }
  return values;
}

function lineAndColumnAt(content, index) {
  const upToIndex = content.slice(0, index);
  const line = (upToIndex.match(/\n/g) ?? []).length + 1;
  const lastNewline = upToIndex.lastIndexOf('\n');
  const column = index - lastNewline;
  return { line, column };
}

function findMatches(content, pattern) {
  const matches = [];
  for (const match of content.matchAll(pattern)) {
    matches.push({ value: match[0], index: match.index });
  }
  return matches;
}

async function main() {
  const files = await collectCssFiles(targetDir);
  const knownTokenLiterals = tokenLiterals(await readFile(tokenSourcePath, 'utf8'));
  const results = [];
  const literalOccurrences = new Map(); // raw literal not represented by a token -> Set(relativePath)

  for (const file of files) {
    const raw = await readFile(file, 'utf8');
    const scanned = stripRootBlocks(raw);
    const relativePath = relative(repositoryRoot, file).replaceAll('\\', '/');

    for (const match of [...findMatches(scanned, HEX_COLOR), ...findMatches(scanned, RAW_PX)]) {
      const normalizedLiteral = match.value.toLowerCase();
      const { line, column } = lineAndColumnAt(raw, match.index);
      const matchingTokens = knownTokenLiterals.get(normalizedLiteral);
      if (matchingTokens) {
        results.push({
          ruleId: 'QS-NG-003',
          level: 'warning',
          message: {
            text: `Raw literal '${match.value}' duplicates ${[...matchingTokens].sort().join(' / ')}; reference the existing central token instead.`,
          },
          locations: [
            {
              physicalLocation: {
                artifactLocation: { uri: relativePath },
                region: { startLine: line, startColumn: column, endLine: line, endColumn: column + match.value.length },
              },
            },
          ],
        });
      }

      // QS-NG-004 is specifically about extending the scale when an uncovered value starts to
      // repeat. Values already covered by a token are reported once per occurrence as QS-NG-003.
      if (!matchingTokens) {
        const bucket = literalOccurrences.get(normalizedLiteral) ?? new Set();
        bucket.add(relativePath);
        literalOccurrences.set(normalizedLiteral, bucket);
      }
    }
  }

  for (const [literal, files] of literalOccurrences) {
    if (files.size < 2) continue;
    results.push({
      ruleId: 'QS-NG-004',
      level: 'note',
      message: {
        text: `Raw literal '${literal}' repeated across ${files.size} stylesheets (${[...files].sort().join(', ')}); extend the token scale in frontend/src/styles.css instead of copying the value.`,
      },
      locations: [...files].sort().map((relativePath) => ({
        physicalLocation: { artifactLocation: { uri: relativePath } },
      })),
    });
  }

  const sarif = {
    $schema: 'https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/Schemata/sarif-schema-2.1.0.json',
    version: '2.1.0',
    runs: [
      {
        tool: {
          driver: {
            name: 'qs-design-tokens-check',
            informationUri: 'https://quality.studio/schemas/rule.v1.schema.json',
            semanticVersion: '1.0.0',
            rules: [
              { id: 'QS-NG-003', name: 'DesignTokensOverRawLiterals' },
              { id: 'QS-NG-004', name: 'ExtendTokenScaleInsteadOfDuplicating' },
            ],
          },
        },
        results,
      },
    ],
  };

  await mkdir(dirname(reportPath), { recursive: true });
  await writeFile(reportPath, JSON.stringify(sarif, null, 2) + '\n', 'utf8');
  console.log(`design-tokens-check: scanned ${files.length} CSS file(s), ${results.length} result(s) -> ${relative(repositoryRoot, reportPath)}`);
}

await main();
