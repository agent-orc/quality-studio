import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const readRepositoryFile = (path) => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

test('required workflow pins the approved clean-checkout toolchain', async () => {
  const [workflow, globalJson, nvmrc] = await Promise.all([
    readRepositoryFile('.github/workflows/build.yml'),
    readRepositoryFile('global.json'),
    readRepositoryFile('.nvmrc'),
  ]);

  assert.match(workflow, /^  pull_request:\s*$/m);
  assert.match(workflow, /DOTNET_VERSION: '10\.0\.301'/);
  assert.match(workflow, /NODE_VERSION: '22\.23\.1'/);
  assert.match(workflow, /GITLEAKS_VERSION: '8\.24\.2'/);
  assert.equal(JSON.parse(globalJson).sdk.version, '10.0.301');
  assert.equal(nvmrc.trim(), '22.23.1');
});

test('required workflow gives every shipping check a distinct step', async () => {
  const workflow = await readRepositoryFile('.github/workflows/build.yml');
  const requiredSteps = [
    'Install frontend dependencies from the lock file',
    'Provision pinned Chromium',
    'Restore .NET solution',
    'Build .NET solution (Release)',
    'Run portable .NET tests',
    'Test required-gate contract',
    'Test browser resolution',
    'Build production Angular bundle',
    'Run Angular tests',
    'Provision pinned Gitleaks',
    'Run repository security scan',
  ];

  for (const step of requiredSteps) {
    assert.ok(workflow.includes(`- name: ${step}`), `missing named workflow step: ${step}`);
  }

  assert.match(workflow, /--filter "Category!=MachineBound"/);
  assert.ok(workflow.indexOf('Provision pinned Gitleaks') < workflow.indexOf('Run repository security scan'));
  assert.ok(workflow.indexOf('Build production Angular bundle') < workflow.indexOf('Run Angular tests'));
});

test('frontend gate preserves the production budget and provisions its pinned browser', async () => {
  const [angularJson, frontendPackage] = await Promise.all([
    readRepositoryFile('frontend/angular.json'),
    readRepositoryFile('frontend/package.json'),
  ]);
  const angular = JSON.parse(angularJson);
  const packageDocument = JSON.parse(frontendPackage);
  const budgets = angular.projects.frontend.architect.build.configurations.production.budgets;
  const initial = budgets.find((budget) => budget.type === 'initial');

  assert.equal(initial.maximumError, '480kB');
  assert.match(packageDocument.scripts['browser:install'], /playwright-core\/cli\.js install chromium/);
  assert.equal(packageDocument.scripts['test:tooling'], 'node --test tests/browser-resolver.test.mjs');
});
