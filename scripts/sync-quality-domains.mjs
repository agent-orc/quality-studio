import { readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const PROPERTY_IDS = ['public-facing', 'html-ui', 'seo-relevant', 'payment-api', 'personal-data', 'authenticated', 'persistent-data', 'realtime', 'localized', 'deployable'];
const DOMAIN_IDS = ['architecture', 'correctness', 'testing', 'security', 'privacy', 'payments', 'reliability', 'performance', 'review-prioritization', 'accessibility', 'seo', 'localization', 'operations'];
const METRIC_METHODS = { 'review-coverage': 'measurement', 'test-coverage': 'measurement', 'risk-view': 'heuristic', 'finding-density': 'measurement', hotspot: 'heuristic', grade: 'review' };
const identifier = /^[a-z][a-z0-9-]*$/;
const fail = message => { throw new Error(message); };
function object(value, fields, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${label} must be an object.`);
  if (Object.keys(value).length !== fields.length || fields.some(field => !Object.hasOwn(value, field))) fail(`${label} has missing or unsupported fields.`);
}
function string(value, label) {
  if (typeof value !== 'string' || !value.trim() || value.length > 10000) fail(`${label} requires nonempty bounded text.`);
}
function localized(value, label) {
  object(value, ['en', 'de'], label);
  string(value.en, `${label}.en`);
  string(value.de, `${label}.de`);
}
function list(value, label) {
  if (!Array.isArray(value) || value.length > 100) fail(`${label} must be a bounded list.`);
}
function references(value, known, label) {
  list(value, label);
  if (new Set(value).size !== value.length) fail(`${label} has duplicate references.`);
  for (const id of value) if (!known.has(id)) fail(`${label} has unknown reference ${id}.`);
}
function uniqueId(value, seen, label) {
  if (typeof value !== 'string' || !identifier.test(value) || seen.has(value)) fail(`${label} has an invalid or duplicate id.`);
  seen.add(value);
}

/** Validate the authored catalogue and its cross-catalogue references, without evaluating applicability. */
export function validateQualityDomains(data, rules, methodology) {
  object(data, ['schemaVersion', 'version', 'applicability', 'sources', 'domains'], 'Catalogue');
  if (data.schemaVersion !== 1 || typeof data.version !== 'string' || !/^\d+\.\d+\.\d+$/.test(data.version)) fail('Unsupported quality-domain version.');
  object(data.applicability, ['properties', 'missing', 'inheritance', 'mode'], 'Applicability contract');
  references(data.applicability.properties, new Set(PROPERTY_IDS), 'Properties');
  if (data.applicability.properties.length !== PROPERTY_IDS.length || data.applicability.missing !== 'unknown' || data.applicability.inheritance !== 'none' || data.applicability.mode !== 'documentation-only') fail('Applicability must remain documentation-only, unknown-preserving and without inheritance.');
  list(data.sources, 'Sources');
  const sourceIds = new Set();
  for (const source of data.sources) {
    object(source, ['id', 'title', 'url'], 'Source');
    uniqueId(source.id, sourceIds, 'Source');
    localized(source.title, `${source.id}.title`);
    let url;
    try { url = new URL(source.url); } catch { fail(`Unsafe source URL: ${source.id}.`); }
    if (url.protocol !== 'https:' || url.username || url.password) fail(`Unsafe source URL: ${source.id}.`);
  }
  const ruleIds = new Set(rules.entries.map(rule => rule.id));
  const metricIds = new Set(methodology.metrics.map(metric => metric.id));
  const seenDomains = new Set();
  const seenChecks = new Set();
  const referencedMetrics = new Set();
  list(data.domains, 'Domains');
  for (const domain of data.domains) {
    object(domain, ['id', 'title', 'why', 'evidence', 'limits', 'checks'], 'Domain');
    uniqueId(domain.id, seenDomains, 'Domain');
    for (const field of ['title', 'why', 'evidence', 'limits']) localized(domain[field], `${domain.id}.${field}`);
    list(domain.checks, `${domain.id}.checks`);
    if (!domain.checks.length) fail(`${domain.id} requires concrete checks.`);
    for (const check of domain.checks) {
      object(check, ['id', 'title', 'status', 'method', 'ruleIds', 'metricIds', 'rationale', 'evidence', 'interpretation', 'limits', 'sourceIds', 'applicability'], 'Check');
      uniqueId(check.id, seenChecks, 'Check');
      for (const field of ['title', 'rationale', 'evidence', 'interpretation', 'limits']) localized(check[field], `${check.id}.${field}`);
      if (!['implemented-metric', 'review-rule', 'planned'].includes(check.status)) fail(`${check.id} has an invalid status.`);
      if (!['measurement', 'heuristic', 'review'].includes(check.method)) fail(`${check.id} has an invalid method.`);
      references(check.ruleIds, ruleIds, `${check.id}.ruleIds`);
      references(check.metricIds, metricIds, `${check.id}.metricIds`);
      references(check.sourceIds, sourceIds, `${check.id}.sourceIds`);
      if (!check.sourceIds.length) fail(`${check.id} requires primary sources.`);
      if (check.status === 'implemented-metric') {
        if (check.metricIds.length !== 1 || check.ruleIds.length) fail(`${check.id} must reference exactly one existing metric and no rules.`);
        const id = check.metricIds[0];
        if (referencedMetrics.has(id)) fail(`Duplicate implemented metric: ${id}.`);
        if (check.method !== METRIC_METHODS[id]) fail(`${id} must preserve its measurement, heuristic or review interpretation.`);
        referencedMetrics.add(id);
      } else if (check.status === 'review-rule') {
        if (!check.ruleIds.length || check.metricIds.length || check.method !== 'review') fail(`${check.id} must reference existing review rules without claiming a metric.`);
      } else if (check.metricIds.length || check.ruleIds.length) fail(`${check.id} is planned and cannot claim an implemented rule or metric.`);
      const selector = check.applicability;
      object(selector, ['subjectScope', 'allOf', 'anyOf', 'noneOf'], `${check.id}.applicability`);
      if (!['project', 'component'].includes(selector.subjectScope)) fail(`${check.id} requires an explicit subject scope.`);
      for (const key of ['allOf', 'anyOf', 'noneOf']) references(selector[key], new Set(PROPERTY_IDS), `${check.id}.${key}`);
      if (selector.allOf.some(id => selector.noneOf.includes(id))) fail(`${check.id} has contradictory property requirements.`);
    }
  }
  if (DOMAIN_IDS.some(id => !seenDomains.has(id))) fail('The catalogue is missing a required quality domain.');
  if (referencedMetrics.size !== metricIds.size || [...metricIds].some(id => !referencedMetrics.has(id))) fail('All existing methodology metrics must be referenced exactly once.');
  return data;
}

export async function syncQualityDomains(root = repositoryRoot, check = false) {
  const read = async relative => JSON.parse(await readFile(join(root, relative), 'utf8'));
  const [data, rules, methodology] = await Promise.all([
    read('rules/quality-domains.json'),
    read('backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json'),
    read('rules/review-methodology.json'),
  ]);
  validateQualityDomains(data, rules, methodology);
  const output = join(root, 'frontend/src/app/features/settings/review-criteria/quality-domains.generated.ts');
  const expected = `// Generated from rules/quality-domains.json; run npm run domains:sync.\nexport const QUALITY_DOMAINS = ${JSON.stringify(data, null, 2)} as const;\n`;
  if (check) {
    const actual = await readFile(output, 'utf8').catch(() => '');
    if (actual.replace(/\r\n/g, '\n') !== expected) fail('Quality domains drifted. Run npm run domains:sync.');
  } else await writeFile(output, expected);
  const checks = data.domains.flatMap(domain => domain.checks);
  console.log(`quality domains: ${data.domains.length} domains, ${checks.length} checks, ${checks.filter(item => item.status === 'implemented-metric').length} existing metrics; ${check ? 'verified' : 'generated'}.`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await syncQualityDomains(repositoryRoot, process.argv.includes('--check'));
}
