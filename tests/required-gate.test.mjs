import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const read = (path) => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

test('required workflow pins tools and runs every portable shipping check', async () => {
  const workflow = await read('.github/workflows/build.yml');
  const requiredContracts = [
    'pull_request:',
    'dotnet-version: 10.0.301',
    'node-version: 22.23.1',
    'dotnet restore QualityStudio.slnx --locked-mode',
    '--filter "Category!=MachineBound"',
    'run: npm ci',
    'npm run browser:install -- --with-deps',
    'npm run build -- --configuration production',
    'run: npm test',
    '-- security provision',
    '-- security scan .',
  ];

  for (const contract of requiredContracts) {
    assert.match(workflow, new RegExp(escapeRegExp(contract)), `missing workflow contract: ${contract}`);
  }

  const stepNames = [...workflow.matchAll(/^\s+- name: (.+)$/gm)].map((match) => match[1]);
  assert.equal(new Set(stepNames).size, stepNames.length, 'every required check must have a distinct step name');
});

test('repository manifests pin the local SDK and browser provisioner', async () => {
  const [sdk, nodeVersion, frontendPackage, angular] = await Promise.all([
    read('global.json').then(JSON.parse),
    read('.nvmrc'),
    read('frontend/package.json').then(JSON.parse),
    read('frontend/angular.json').then(JSON.parse),
  ]);

  assert.deepEqual(sdk.sdk, { version: '10.0.301', rollForward: 'disable' });
  assert.equal(nodeVersion.trim(), '22.23.1');
  assert.equal(frontendPackage.engines.node, '22.x');
  assert.equal(frontendPackage.scripts['browser:install'], 'playwright-core install chromium');
  assert.equal(frontendPackage.scripts.test, 'node ./tests/run-tests.mjs');

  const budgets = angular.projects.frontend.architect.build.configurations.production.budgets;
  const initial = budgets.find((budget) => budget.type === 'initial');
  assert.equal(initial.maximumError, '480kB');
});

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
