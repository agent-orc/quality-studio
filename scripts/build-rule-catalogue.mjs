// Compiles rules/**/*.md (frontmatter + body) into the built-in rule catalogue
// consumed by RuleCatalogueResolver at runtime. See rules/README.md.
//
// Usage:
//   node scripts/build-rule-catalogue.mjs           regenerate the catalogue
//   node scripts/build-rule-catalogue.mjs --check    fail if the checked-in catalogue is stale

import { readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const rulesRoot = join(repositoryRoot, 'rules');
const outputPath = join(repositoryRoot, 'src', 'AgentOrchestrator.CodeQuality', 'catalogues', 'rule-catalogue.v1.json');
const CATALOGUE_VERSION = '1.0.0';

const SEVERITIES = new Set(['critical', 'high', 'medium', 'low', 'info']);
const TIERS = new Set(['core', 'extended']);
const STATUSES = new Set(['active', 'deprecated']);
const KINDS = new Set(['code', 'security', 'performance']);
const ID_PATTERN = /^QS-[A-Z]{2,4}-[0-9]{3}$/;
const REQUIRED_SECTIONS = ['Statement', 'Rationale', 'Good example', 'Bad example'];

async function main() {
  const checkOnly = process.argv.includes('--check');
  const files = await findRuleFiles(rulesRoot);
  if (files.length === 0) throw new Error(`No rule files found under ${rulesRoot}.`);

  const entries = [];
  const errors = [];
  for (const file of files) {
    try {
      entries.push(await parseRuleFile(file));
    } catch (error) {
      errors.push(`${relative(file)}: ${error.message}`);
    }
  }
  if (errors.length > 0) {
    throw new Error(`Invalid rule file(s):\n  ${errors.join('\n  ')}`);
  }

  const seen = new Map();
  for (const entry of entries) {
    const existing = seen.get(entry.id);
    if (existing) throw new Error(`Duplicate rule id '${entry.id}' in ${existing} and ${entry.source}.`);
    seen.set(entry.id, entry.source);
  }

  entries.sort((a, b) => a.id.localeCompare(b.id, 'en'));
  const document = {
    $schema: 'https://quality.studio/schemas/quality-rule-catalogue.v1.schema.json',
    schemaVersion: 1,
    catalogueVersion: CATALOGUE_VERSION,
    entries: entries.map(toCatalogueEntry),
  };
  const rendered = JSON.stringify(document, null, 2) + '\n';

  if (checkOnly) {
    const current = await readFile(outputPath, 'utf8').catch(() => null);
    if (current !== rendered) {
      throw new Error(
        `${relative(outputPath)} is stale. Run 'node scripts/build-rule-catalogue.mjs' and commit the result.`);
    }
    console.log(`${relative(outputPath)} is up to date (${entries.length} rules).`);
    return;
  }

  await writeFile(outputPath, rendered, 'utf8');
  console.log(`Wrote ${relative(outputPath)} (${entries.length} rules).`);
}

async function findRuleFiles(root) {
  const areas = await readdir(root, { withFileTypes: true });
  const files = [];
  for (const area of areas) {
    if (!area.isDirectory()) continue;
    const areaPath = join(root, area.name);
    for (const entry of await readdir(areaPath, { withFileTypes: true })) {
      if (entry.isFile() && entry.name.endsWith('.md')) files.push(join(areaPath, entry.name));
    }
  }
  return files.sort();
}

async function parseRuleFile(path) {
  const text = (await readFile(path, 'utf8')).replace(/\r\n/g, '\n');
  if (!text.startsWith('---\n')) throw new Error("must start with YAML frontmatter ('---')");
  const end = text.indexOf('\n---\n', 4);
  if (end < 0) throw new Error('has unterminated frontmatter');

  const frontmatter = parseFrontmatter(text.slice(4, end));
  const body = text.slice(end + 5);
  const sections = parseSections(body);

  const entry = {
    source: relative(path),
    id: field('id'),
    version: frontmatter.version ?? '1.0.0',
    title: field('title'),
    technology: field('technology'),
    category: field('category'),
    kinds: listField('kinds'),
    severity: field('severity'),
    autofixable: booleanField('autofixable'),
    tier: field('tier'),
    status: field('status'),
    since: field('since'),
    statement: section('Statement'),
    rationale: section('Rationale'),
    goodExample: section('Good example'),
    badExample: section('Bad example'),
  };

  if (!ID_PATTERN.test(entry.id)) throw new Error(`id '${entry.id}' does not match ${ID_PATTERN}`);
  if (basenamePrefix(path) !== entry.id)
    throw new Error(`file name does not start with its own id '${entry.id}'`);
  if (!SEVERITIES.has(entry.severity)) throw new Error(`severity '${entry.severity}' is not one of ${[...SEVERITIES]}`);
  if (!TIERS.has(entry.tier)) throw new Error(`tier '${entry.tier}' is not one of ${[...TIERS]}`);
  if (!STATUSES.has(entry.status)) throw new Error(`status '${entry.status}' is not one of ${[...STATUSES]}`);
  for (const kind of entry.kinds) {
    if (!KINDS.has(kind)) throw new Error(`kind '${kind}' is not one of ${[...KINDS]}`);
  }
  return entry;

  function field(key) {
    const value = frontmatter[key];
    if (value === undefined || value === '') throw new Error(`frontmatter is missing '${key}'`);
    return value;
  }
  function booleanField(key) {
    const value = field(key);
    if (value !== 'true' && value !== 'false') throw new Error(`frontmatter '${key}' must be true or false`);
    return value === 'true';
  }
  function listField(key) {
    const value = field(key);
    if (!value.startsWith('[') || !value.endsWith(']')) throw new Error(`frontmatter '${key}' must be a [bracketed, list]`);
    return value
      .slice(1, -1)
      .split(',')
      .map(item => item.trim())
      .filter(item => item.length > 0);
  }
  function section(name) {
    const value = sections[name];
    if (!value) throw new Error(`is missing the '## ${name}' section`);
    return value;
  }
}

function parseFrontmatter(raw) {
  const fields = {};
  for (const line of raw.split('\n')) {
    if (line.trim().length === 0) continue;
    const separator = line.indexOf(':');
    if (separator <= 0) throw new Error(`has invalid frontmatter line '${line}'`);
    fields[line.slice(0, separator).trim()] = line.slice(separator + 1).trim();
  }
  return fields;
}

function parseSections(body) {
  const sections = {};
  const headingPattern = /^##\s+(.+?)\s*$/gm;
  const matches = [...body.matchAll(headingPattern)];
  for (let index = 0; index < matches.length; index += 1) {
    const heading = matches[index][1].trim();
    if (!REQUIRED_SECTIONS.includes(heading)) continue;
    const start = matches[index].index + matches[index][0].length;
    const end = index + 1 < matches.length ? matches[index + 1].index : body.length;
    sections[heading] = body.slice(start, end).trim();
  }
  return sections;
}

function basenamePrefix(path) {
  const name = path.split('/').pop();
  return name.split('-').slice(0, 3).join('-');
}

function relative(path) {
  return path.startsWith(repositoryRoot) ? path.slice(repositoryRoot.length + 1) : path;
}

function toCatalogueEntry(entry) {
  return {
    id: entry.id,
    version: entry.version,
    title: entry.title,
    technology: entry.technology,
    category: entry.category,
    kinds: entry.kinds,
    severity: entry.severity,
    autofixable: entry.autofixable,
    tier: entry.tier,
    status: entry.status,
    since: entry.since,
    statement: entry.statement,
    rationale: entry.rationale,
    goodExample: entry.goodExample,
    badExample: entry.badExample,
  };
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
