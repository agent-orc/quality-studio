// Generates the built-in named-rule catalogue from the authored Markdown rule tree.
//
// Source of truth: rules/<technology>/<id>-<slug>.md plus rules/CHANGELOG.md for the
// library version. Target: src/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json,
// embedded into the analysis core assembly and read by RuleCatalogueResolver.
//
// The output is a pure function of the rule tree: entries are sorted by id, object keys are
// emitted in a fixed order, and nothing time- or checkout-dependent is written. A commit hash
// would make the file drift on every commit and turn `rules:check` into a false alarm, so the
// generated document records none.
//
// Usage:
//   node scripts/sync-rule-catalogue.mjs            regenerate the catalogue
//   node scripts/sync-rule-catalogue.mjs --check    fail (exit 1) when the catalogue has drifted

import { readdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const rulesDirectory = join(repositoryRoot, 'rules');
const schemaPath = join(repositoryRoot, 'schemas', 'rule-catalogue.v1.schema.json');
const targetPath = join(
  repositoryRoot, 'src', 'AgentOrchestrator.CodeQuality', 'catalogues', 'rule-catalogue.v1.json');
const schemaId = 'https://agent-orchestrator.dev/quality/schemas/rule-catalogue.v1.schema.json';

const technologies = new Map([['angular', 'QS-NG'], ['dotnet', 'QS-CS'], ['generic', 'QS-GN']]);
const requiredSections = ['Statement', 'Rationale', 'Detection', 'Good example', 'Bad example', 'Change history'];
const entryKeyOrder = [
  'id', 'version', 'title', 'technology', 'category', 'kinds', 'statement', 'rationale', 'detection',
  'goodExample', 'badExample', 'severity', 'defaultOn', 'autofixable', 'deterministicRuleIds',
  'relatedGuideline', 'since', 'changeHistory', 'enabled',
];

const failures = [];
function fail(message) {
  failures.push(message);
}

function collapse(text) {
  return text.replace(/\s+/g, ' ').trim();
}

/** Parses the small `key: value` frontmatter dialect the rule files and review inputs share. */
function parseFrontmatter(text, file) {
  if (!text.startsWith('---\n')) {
    fail(`${file}: must start with YAML frontmatter.`);
    return null;
  }
  const end = text.indexOf('\n---\n', 4);
  if (end < 0) {
    fail(`${file}: has unterminated frontmatter.`);
    return null;
  }
  const fields = new Map();
  for (const line of text.slice(4, end).split('\n')) {
    if (line.trim().length === 0) continue;
    const separator = line.indexOf(':');
    if (separator <= 0) {
      fail(`${file}: invalid frontmatter line '${line}'.`);
      continue;
    }
    const key = line.slice(0, separator).trim();
    const raw = line.slice(separator + 1).trim();
    if (raw.startsWith('[') && raw.endsWith(']')) {
      fields.set(key, raw.slice(1, -1).split(',').map(value => value.trim().replace(/^["']|["']$/g, ''))
        .filter(value => value.length > 0));
    } else if (raw === 'true' || raw === 'false') {
      fields.set(key, raw === 'true');
    } else {
      fields.set(key, raw.replace(/^["']|["']$/g, ''));
    }
  }
  return { fields, body: text.slice(end + 5) };
}

/** Splits the rule body into its `## `-delimited sections. */
function parseSections(body, file) {
  const sections = new Map();
  let current = null;
  const lines = [];
  const flush = () => {
    if (current !== null) sections.set(current, lines.join('\n').trim());
    lines.length = 0;
  };
  for (const line of body.split('\n')) {
    if (line.startsWith('## ')) {
      flush();
      current = line.slice(3).trim();
      continue;
    }
    if (current !== null) lines.push(line);
  }
  flush();
  for (const section of requiredSections) {
    if (!sections.has(section) || sections.get(section).length === 0) {
      fail(`${file}: requires a non-empty '## ${section}' section.`);
    }
  }
  return sections;
}

/** Rule examples are the fenced code block under their heading, without the fence. */
function parseExample(sections, heading, file) {
  const section = sections.get(heading) ?? '';
  const match = /```[a-z]*\n([\s\S]*?)```/.exec(section);
  if (!match) {
    fail(`${file}: '## ${heading}' requires a fenced code block.`);
    return '';
  }
  return match[1].replace(/\s+$/, '');
}

function parseChangeHistory(sections, file) {
  const entries = [];
  const section = sections.get('Change history') ?? '';
  for (const item of section.split(/\n(?=- )/)) {
    const text = collapse(item);
    if (text.length === 0) continue;
    const match = /^- (\S+) \((\d{4}-\d{2}-\d{2})\): (.+)$/.exec(text);
    if (!match) {
      fail(`${file}: change-history item '${text}' must read '- <version> (<yyyy-mm-dd>): <change>'.`);
      continue;
    }
    entries.push({ version: match[1], date: match[2], change: match[3] });
  }
  if (entries.length === 0) fail(`${file}: requires at least one change-history item.`);
  return entries;
}

function requireString(fields, key, file) {
  const value = fields.get(key);
  if (typeof value !== 'string' || value.length === 0) {
    fail(`${file}: requires a non-empty '${key}'.`);
    return '';
  }
  return value;
}

function requireBoolean(fields, key, file) {
  const value = fields.get(key);
  if (typeof value !== 'boolean') {
    fail(`${file}: requires '${key}' to be true or false.`);
    return false;
  }
  return value;
}

function requireList(fields, key, file) {
  const value = fields.get(key);
  if (!Array.isArray(value) || value.length === 0) {
    fail(`${file}: requires a non-empty '${key}' list.`);
    return [];
  }
  return value;
}

function readRule(technology, fileName, text) {
  const file = `rules/${technology}/${fileName}`;
  const parsed = parseFrontmatter(text.replace(/\r\n/g, '\n'), file);
  if (parsed === null) return null;
  const { fields, body } = parsed;
  const sections = parseSections(body, file);
  const id = requireString(fields, 'id', file);
  const prefix = technologies.get(technology);
  if (id.length > 0 && !id.startsWith(`${prefix}-`)) {
    fail(`${file}: id '${id}' does not use the '${prefix}-' prefix of its '${technology}' directory.`);
  }
  if (id.length > 0 && !fileName.startsWith(`${id}-`)) {
    fail(`${file}: file name must start with '${id}-'.`);
  }
  if (fields.get('technology') !== technology) {
    fail(`${file}: technology '${fields.get('technology')}' does not match its '${technology}' directory.`);
  }

  const version = requireString(fields, 'version', file);
  const changeHistory = parseChangeHistory(sections, file);
  if (changeHistory.length > 0 && changeHistory[0].version !== version) {
    fail(`${file}: newest change-history entry is ${changeHistory[0].version}, but version is ${version}.`);
  }
  const relatedGuideline = fields.get('relatedGuideline');
  return {
    id,
    version,
    title: requireString(fields, 'title', file),
    technology,
    category: requireString(fields, 'category', file),
    kinds: requireList(fields, 'kinds', file),
    statement: collapse(sections.get('Statement') ?? ''),
    rationale: collapse(sections.get('Rationale') ?? ''),
    detection: collapse(sections.get('Detection') ?? ''),
    goodExample: parseExample(sections, 'Good example', file),
    badExample: parseExample(sections, 'Bad example', file),
    severity: requireString(fields, 'severity', file),
    defaultOn: requireBoolean(fields, 'defaultOn', file),
    autofixable: requireBoolean(fields, 'autofixable', file),
    deterministicRuleIds: Array.isArray(fields.get('deterministicRuleIds')) ? fields.get('deterministicRuleIds') : [],
    relatedGuideline: typeof relatedGuideline === 'string' && relatedGuideline.length > 0 ? relatedGuideline : null,
    since: requireString(fields, 'since', file),
    changeHistory,
    enabled: fields.has('enabled') ? requireBoolean(fields, 'enabled', file) : true,
  };
}

async function readCatalogueVersion() {
  const changelog = await readFile(join(rulesDirectory, 'CHANGELOG.md'), 'utf8');
  const match = /^## (\d+\.\d+\.\d+)/m.exec(changelog);
  if (!match) {
    fail('rules/CHANGELOG.md: no `## <version>` heading found; it carries the library version.');
    return '0.0.0';
  }
  return match[1];
}

async function readRules() {
  const entries = [];
  for (const technology of [...technologies.keys()].sort()) {
    let files;
    try {
      files = await readdir(join(rulesDirectory, technology));
    } catch {
      continue;
    }
    for (const fileName of files.filter(name => name.endsWith('.md')).sort()) {
      const text = await readFile(join(rulesDirectory, technology, fileName), 'utf8');
      const rule = readRule(technology, fileName, text);
      if (rule !== null) entries.push(rule);
    }
  }
  entries.sort((left, right) => (left.id < right.id ? -1 : left.id > right.id ? 1 : 0));
  const seen = new Set();
  for (const entry of entries) {
    if (seen.has(entry.id)) fail(`Duplicate rule id '${entry.id}'.`);
    seen.add(entry.id);
  }
  return entries;
}

/**
 * Validates the generated document against the subset of JSON Schema draft 2020-12 the rule
 * catalogue schema uses, so the schema stays the contract without pulling in an npm dependency.
 */
function validate(schema, value, path, root) {
  if (schema.$ref) {
    const target = schema.$ref.replace(/^#\//, '').split('/')
      .reduce((node, segment) => node?.[segment], root);
    if (!target) throw new Error(`Unresolvable $ref '${schema.$ref}'.`);
    validate(target, value, path, root);
    return;
  }
  if (schema.const !== undefined && value !== schema.const) {
    fail(`${path}: expected ${JSON.stringify(schema.const)}, found ${JSON.stringify(value)}.`);
  }
  if (schema.enum && !schema.enum.includes(value)) {
    fail(`${path}: '${value}' is not one of ${schema.enum.join(', ')}.`);
  }
  const types = schema.type === undefined ? [] : [schema.type].flat();
  if (types.length > 0 && !types.some(type => matchesType(type, value))) {
    fail(`${path}: expected ${types.join(' or ')}, found ${value === null ? 'null' : typeof value}.`);
    return;
  }
  if (typeof value === 'string') {
    if (schema.minLength !== undefined && value.length < schema.minLength) {
      fail(`${path}: shorter than ${schema.minLength} characters.`);
    }
    if (schema.pattern !== undefined && !new RegExp(schema.pattern).test(value)) {
      fail(`${path}: '${value}' does not match ${schema.pattern}.`);
    }
    if (schema.format === 'date' && !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
      fail(`${path}: '${value}' is not an ISO date.`);
    }
  }
  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) {
      fail(`${path}: requires at least ${schema.minItems} items.`);
    }
    if (schema.items) value.forEach((item, index) => validate(schema.items, item, `${path}[${index}]`, root));
    return;
  }
  if (value !== null && typeof value === 'object') {
    for (const key of schema.required ?? []) {
      if (!Object.hasOwn(value, key)) fail(`${path}: missing required property '${key}'.`);
    }
    if (schema.additionalProperties === false) {
      for (const key of Object.keys(value)) {
        if (!Object.hasOwn(schema.properties ?? {}, key)) fail(`${path}: unexpected property '${key}'.`);
      }
    }
    for (const [key, child] of Object.entries(schema.properties ?? {})) {
      if (Object.hasOwn(value, key)) validate(child, value[key], `${path}.${key}`, root);
    }
  }
}

function matchesType(type, value) {
  switch (type) {
    case 'object': return value !== null && typeof value === 'object' && !Array.isArray(value);
    case 'array': return Array.isArray(value);
    case 'string': return typeof value === 'string';
    case 'boolean': return typeof value === 'boolean';
    case 'integer': return Number.isInteger(value);
    case 'number': return typeof value === 'number';
    case 'null': return value === null;
    default: throw new Error(`Unsupported schema type '${type}'.`);
  }
}

function orderKeys(entry) {
  const ordered = {};
  for (const key of entryKeyOrder) ordered[key] = entry[key];
  return ordered;
}

const catalogueVersion = await readCatalogueVersion();
const entries = await readRules();
const document = {
  $schema: schemaId,
  schemaVersion: 1,
  catalogueVersion,
  entries: entries.map(orderKeys),
};
const schema = JSON.parse(await readFile(schemaPath, 'utf8'));
validate(schema, document, 'rule-catalogue', schema);

const rendered = `${JSON.stringify(document, null, 2)}\n`;
if (failures.length > 0) {
  for (const message of failures) console.error(message);
  console.error(`rules: ${failures.length} problem(s) in the rule library; nothing was written.`);
  process.exit(1);
}

if (process.argv.slice(2).includes('--check')) {
  let current = null;
  try {
    current = await readFile(targetPath, 'utf8');
  } catch {
    console.error(`rules: ${targetPath} is missing. Run npm run rules:sync.`);
    process.exit(1);
  }
  if (current !== rendered) {
    console.error('rules: the generated catalogue has drifted from rules/. Run npm run rules:sync.');
    process.exit(1);
  }
  console.log(`rules: catalogue ${catalogueVersion} matches ${entries.length} authored rules.`);
} else {
  await writeFile(targetPath, rendered);
  console.log(`rules: wrote catalogue ${catalogueVersion} with ${entries.length} rules.`);
}
