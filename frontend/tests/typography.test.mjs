import assert from 'node:assert/strict';
import { test } from 'node:test';
import path from 'node:path';
import { ESLint } from 'eslint';
import { checkTypography, typographyProcessor, typographyRule, typographyRuleId } from '../lint/typography.mjs';
import { typographyContract } from '../lint/typography.config.mjs';

test('CSS parser detects font-size, shorthand and token drift at source locations', () => {
  const results = checkTypography(`/* font-size: 8px is only a comment */
.label { font-size: 10px; }
.label::before { content: 'font: 8px Arial'; }
.control { font: 600 9px/1.2 Arial; }
:root { --studio-font-size-label: 8px; --unrelated-size: 4px; }`, 'fixture.css', typographyContract);
  assert.equal(results.length, 3);
  assert.deepEqual(results.map(result => result.line), [2, 4, 5]);
  assert.ok(results.every(result => result.ruleId === typographyRuleId && result.column > 0));
});

test('valid sizes, line-height, strings and relative values are not false positives', () => {
  const results = checkTypography(`
.a { font-size: 11px; font: 600 13px/8px '9px'; line-height: 8px; }
.b { font-size: var(--studio-font-size-label); font-size: calc(9px + 5px); font-size: .8rem; }
.c { --studio-font-size-body: 13px; padding: 4px; content: 'font-size: 5px'; }
/* .ignored { font-size: 3px; } */`, 'fixture.css', typographyContract);
  assert.deepEqual(results, []);
});

test('scientific and fractional literal px values and zero cannot bypass minimum', () => {
  const results = checkTypography('.a { font-size: 1e1px; font: italic 10.5px Arial; font-size: 0; }',
    'fixture.css', typographyContract);
  assert.equal(results.length, 3);
});

test('decorative exception is exact and reasoned', () => {
  const contract = { ...typographyContract,
    exceptions: [{ file: 'fixture.css', selector: '.decorative', property: 'font-size', reason: 'Icon geometry only.' }] };
  const css = '.decorative { font-size: 8px; } .body { font-size: 8px; }';
  assert.equal(checkTypography(css, 'fixture.css', contract).length, 1);
  assert.equal(checkTypography(css, 'other.css', contract).length, 2);
});

test('invalid CSS makes the check fail visibly', () => {
  const [result] = checkTypography('.broken { font-size: 8px;', 'fixture.css', typographyContract);
  assert.equal(result.fatal, true);
  assert.match(result.message, /Cannot check/);
});

test('ESLint exposes CSS typography diagnostics as native source-located SARIF findings', async () => {
  const eslint = new ESLint({
    overrideConfigFile: true,
    overrideConfig: [{
      files: ['**/*.css'],
      plugins: { 'quality-architecture': { rules: { 'minimum-font-size': typographyRule } } },
      rules: { [typographyRuleId]: 'error' },
      processor: typographyProcessor(typographyContract),
    }],
  });
  const results = await eslint.lintText('.label { font-size: 9px; }', { filePath: 'typography-fixture.css' });
  assert.equal(results[0].errorCount, 1);
  const formatter = await eslint.loadFormatter(path.resolve('node_modules/@microsoft/eslint-formatter-sarif/sarif.js'));
  const sarif = JSON.parse(await formatter.format(results));
  const finding = sarif.runs[0].results[0];
  assert.equal(finding.ruleId, typographyRuleId);
  assert.equal(finding.locations[0].physicalLocation.region.startLine, 1);
  assert.match(finding.locations[0].physicalLocation.artifactLocation.uri, /typography-fixture.css$/);
});
