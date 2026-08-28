import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const workflowPath = fileURLToPath(new URL('../.github/workflows/build.yml', import.meta.url));
const workflow = readFileSync(workflowPath, 'utf8');

function stepIndex(name) {
  const marker = `- name: ${name}`;
  const index = workflow.indexOf(marker);
  assert.ok(index >= 0, `expected a step named "${name}" in build.yml`);
  return index;
}

test('checkout uses full history for the Prettier changed-file gate', () => {
  const checkout = workflow.indexOf('- name: Check out repository');
  const nextStep = workflow.indexOf('- name:', checkout + 1);
  const checkoutBlock = workflow.slice(checkout, nextStep);
  assert.match(checkoutBlock, /fetch-depth:\s*0/);
});

test('frontend dependencies install before the style gates run', () => {
  const install = stepIndex('Install frontend dependencies');
  const dotnetGate = stepIndex('Check .NET whitespace baseline');
  const eslintGate = stepIndex('Check ESLint baseline');
  const formatGate = stepIndex('Check changed-file frontend formatting');

  assert.ok(install < dotnetGate);
  assert.ok(install < eslintGate);
  assert.ok(install < formatGate);
});

test('style gates run after the .NET build and before the security scan', () => {
  const build = stepIndex('Build');
  const dotnetGate = stepIndex('Check .NET whitespace baseline');
  const eslintGate = stepIndex('Check ESLint baseline');
  const formatGate = stepIndex('Check changed-file frontend formatting');
  const securityScan = stepIndex('Security scan');

  assert.ok(build < dotnetGate);
  assert.ok(dotnetGate < eslintGate);
  assert.ok(eslintGate < formatGate);
  assert.ok(formatGate < securityScan);
});

test('style gates run the corresponding npm scripts', () => {
  assert.match(workflow, /Check \.NET whitespace baseline\s*\n\s*run: npm run style:dotnet:check/);
  assert.match(workflow, /Check ESLint baseline\s*\n\s*run: npm run style:eslint:check/);
  assert.match(workflow, /Check changed-file frontend formatting\s*\n\s*run: npm run style:frontend:check/);
});
