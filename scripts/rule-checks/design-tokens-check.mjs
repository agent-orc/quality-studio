#!/usr/bin/env node
// Deterministic pre-check for QS-NG-003 / QS-NG-004 (rules/angular). Scans CSS for raw hex colors
// and raw px literals that exactly duplicate a :root custom-property token, and for the same raw
// literal repeated across two or more stylesheets. Emits a SARIF 2.1.0 report
// (schemas/sarif-2.1.0-output.schema.json) that
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
const targetDir = resolve(repositoryRoot, process.argv[2] ?? 'frontend/src');
const reportPath = resolve(repositoryRoot, process.argv[3] ?? '.quality/rule-checks/design-tokens.sarif.json');

const HEX_COLOR = /#[0-9a-fA-F]{3,8}\b/g;
// Raw px values, excluding the accepted 1px hairline-border exception (QS-NG-003).
const RAW_PX = /(?<![\w-])(?!1px\b)\d+(?:\.\d+)?px\b/g;
const ROOT_BLOCK = /:root(?:\[[^\]]*])?\s*\{[^}]*\}/g;
const CUSTOM_PROPERTY = /(--[\w-]+)\s*:\s*([^;}]+)/g;
const TOKEN_LITERAL = /^(?:#[0-9a-fA-F]{3,8}|\d+(?:\.\d+)?px)$/;

async function collectCssFiles(path) {
  const metadata = await stat(path);
  if (metadata.isFile()) return path.endsWith('.css') ? [path] : [];
  const entries = await readdir(path, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    if (entry.name === 'node_modules' || entry.name.startsWith('.')) continue;
    const full = join(path, entry.name);
    if (entry.isDirectory()) files.push(...(await collectCssFiles(full)));
    else if (entry.name.endsWith('.css')) files.push(full);
  }
  return files;
}

function stripIgnoredContent(content) {
  // Token declarations themselves (":root { --studio-space-1: 4px; ... }") are the source of
  // truth, not a violation. Comments are not executable CSS. Replace both with same-length spaces
  // so SARIF line and column positions still match the original source.
  return content
    .replace(/\/\*[\s\S]*?\*\//g, (match) => match.replace(/[^\n]/g, ' '))
    .replace(ROOT_BLOCK, (match) => match.replace(/[^\n]/g, ' '));
}

function tokenNamesByLiteral(contents) {
  const tokens = new Map();
  for (const content of contents) {
    for (const root of content.matchAll(ROOT_BLOCK)) {
      for (const declaration of root[0].matchAll(CUSTOM_PROPERTY)) {
        const value = declaration[2].trim();
        if (!TOKEN_LITERAL.test(value) || value === '1px') continue;
        const key = value.toLowerCase();
        const names = tokens.get(key) ?? new Set();
        names.add(declaration[1]);
        tokens.set(key, names);
      }
    }
  }
  return tokens;
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
  const sources = await Promise.all(files.map(async (file) => ({ file, raw: await readFile(file, 'utf8') })));
  const tokenNames = tokenNamesByLiteral(sources.map((source) => source.raw));
  const results = [];
  const literalOccurrences = new Map(); // raw literal -> Set(relativePath), for QS-NG-004 duplication

  for (const { file, raw } of sources) {
    const scanned = stripIgnoredContent(raw);
    const relativePath = relative(repositoryRoot, file).replaceAll('\\', '/');

    for (const match of [...findMatches(scanned, HEX_COLOR), ...findMatches(scanned, RAW_PX)]) {
      const { line, column } = lineAndColumnAt(raw, match.index);
      const matchingTokens = tokenNames.get(match.value.toLowerCase());
      if (matchingTokens) {
        results.push({
          ruleId: 'QS-NG-003',
          level: 'warning',
          message: {
            text: `Raw literal '${match.value}' duplicates ${[...matchingTokens].sort().join(' / ')}; use the design token instead.`,
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

      const bucket = literalOccurrences.get(match.value) ?? new Set();
      bucket.add(relativePath);
      literalOccurrences.set(match.value, bucket);
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
