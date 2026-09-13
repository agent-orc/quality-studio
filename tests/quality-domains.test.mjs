import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateQualityDomains, syncQualityDomains, PROPERTY_IDS } from '../scripts/sync-quality-domains.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const read = async relative => JSON.parse(await readFile(new URL('../' + relative, import.meta.url), 'utf8'));
const data = await read('rules/quality-domains.json');
const rules = await read('backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json');
const methodology = await read('rules/review-methodology.json');
const schema = await read('schemas/quality-domains.v1.schema.json');
const mutate = change => { const copy = structuredClone(data); change(copy); return copy; };
const validate = value => validateQualityDomains(value, rules, methodology);

test('the generated Angular catalogue matches all authored domains and existing metrics', async () => {
  validate(data);
  await syncQualityDomains(root, true);
  assert.equal(data.domains.length, 13);
  assert.equal(data.domains.flatMap(domain => domain.checks).filter(check => check.status === 'implemented-metric').length, 6);
  assert.deepEqual(schema.$defs.property.enum, PROPERTY_IDS);
});

test('status cannot turn planned work or reviewer judgment into a measured product capability', () => {
  assert.throws(() => validate(mutate(value => { value.domains.find(domain => domain.id === 'payments').checks[0].status = 'implemented-metric'; })), /exactly one existing metric/);
  assert.throws(() => validate(mutate(value => { value.domains.find(domain => domain.id === 'seo').checks[0].metricIds = ['grade']; })), /without claiming a metric/);
  assert.throws(() => validate(mutate(value => { value.domains.find(domain => domain.id === 'correctness').checks[1].method = 'measurement'; })), /interpretation/);
  assert.throws(() => validate(mutate(value => { value.domains[0].checks[0].formula = '100 - bugs'; })), /unsupported fields/);
});

test('references reject missing rules, metrics, sources, duplicate IDs and unsafe links', () => {
  for (const field of ['ruleIds', 'metricIds', 'sourceIds']) {
    assert.throws(() => validate(mutate(value => { value.domains[0].checks[0][field] = ['missing']; })), /unknown reference/);
  }
  assert.throws(() => validate(mutate(value => { value.domains[0].checks.push(value.domains[0].checks[0]); })), /duplicate id/);
  assert.throws(() => validate(mutate(value => { value.sources[0].url = 'javascript:alert(1)'; })), /Unsafe source URL/);
  assert.throws(() => validate(mutate(value => { value.sources[0].url = 'https://user:password@example.org/'; })), /Unsafe source URL/);
});

test('property declarations remain local, explicit and documentation-only', () => {
  assert.throws(() => validate(mutate(value => { value.applicability.missing = 'false'; })), /unknown-preserving/);
  assert.throws(() => validate(mutate(value => { value.applicability.inheritance = 'project'; })), /without inheritance/);
  assert.throws(() => validate(mutate(value => { value.applicability.mode = 'runtime'; })), /documentation-only/);
  assert.throws(() => validate(mutate(value => { value.domains[0].checks[0].applicability.allOf = ['unknown-property']; })), /unknown reference/);
  assert.throws(() => validate(mutate(value => { value.domains[0].checks[0].applicability.subjectScope = 'inherited'; })), /explicit subject scope/);
  assert.throws(() => validate(mutate(value => { value.domains[0].title.de = ''; })), /nonempty bounded text/);
});

test('drift checks tolerate checkout CRLF but still reject changed generated content', async () => {
  const fixture = await mkdtemp(join(tmpdir(), 'quality-domains-'));
  try {
    for (const [relative, value] of [
      ['rules/quality-domains.json', data],
      ['rules/review-methodology.json', methodology],
      ['backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json', rules],
    ]) {
      await mkdir(join(fixture, relative, '..'), { recursive: true });
      await writeFile(join(fixture, relative), JSON.stringify(value));
    }
    const generated = 'frontend/src/app/features/settings/review-criteria/quality-domains.generated.ts';
    await mkdir(join(fixture, generated, '..'), { recursive: true });
    await syncQualityDomains(fixture);
    const text = await readFile(join(fixture, generated), 'utf8');
    await writeFile(join(fixture, generated), text.replace(/\n/g, '\r\n'));
    await syncQualityDomains(fixture, true);
    await writeFile(join(fixture, generated), text.replace('Architecture', 'Changed architecture'));
    await assert.rejects(syncQualityDomains(fixture, true), /drifted/);
  } finally { await rm(fixture, { recursive: true, force: true }); }
});
