import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { renderReference, syncReviewReference, validateReference } from '../scripts/sync-review-reference.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const methodology = JSON.parse(await readFile(new URL('../rules/review-methodology.json', import.meta.url), 'utf8'));
const catalogue = JSON.parse(await readFile(new URL('../backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json', import.meta.url), 'utf8'));

test('the tool and website match the authored methodology and rule library', async () => {
  await syncReviewReference(root, true);
});

test('every method references existing rules and authoritative HTTPS sources', () => {
  validateReference(methodology, catalogue);
  const broken = structuredClone(methodology);
  broken.principles[0].ruleIds.push('missing-rule');
  assert.throws(() => validateReference(broken, catalogue), /Unknown rule/);
  const unsafe = structuredClone(methodology);
  unsafe.sources[0].url = 'javascript:alert(1)';
  assert.throws(() => validateReference(unsafe, catalogue), /Unsafe reference URL/);
});

test('website reference escapes rule prose and worked examples instead of creating active HTML', () => {
  const unsafe = structuredClone(catalogue);
  unsafe.entries[0].goodExample = '<script>alert(1)</script>';
  const html = renderReference(methodology, unsafe);
  assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
  assert.ok(!html.includes('<script>alert(1)</script>'));
});

test('every metric has a formula, interpretation, limitations and a real implementation', async () => {
  for (const metric of methodology.metrics) {
    assert.ok(metric.formula && metric.interpretation && metric.limits);
    await readFile(new URL('../' + metric.implementation, import.meta.url));
  }
});
