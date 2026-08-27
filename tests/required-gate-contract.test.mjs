import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

async function read(relativePath) {
  return readFile(fileURLToPath(new URL(`../${relativePath}`, import.meta.url)), 'utf8');
}

test('required workflow pins tools and exposes each portable product check', async () => {
  const workflow = await read('.github/workflows/build.yml');

  assert.match(workflow, /pull_request:\n    branches: \[main\]/);
  assert.match(workflow, /dotnet-version: 10\.0\.301/);
  assert.match(workflow, /node-version: 22\.23\.1/);
  assert.match(workflow, /dotnet restore QualityStudio\.slnx --locked-mode/);
  assert.match(workflow, /--filter "Category!=MachineBound"/);
  assert.match(workflow, /- name: Install locked frontend dependencies\n        run: npm --prefix frontend ci/);
  assert.match(workflow, /- name: Provision pinned Playwright Chromium/);
  assert.match(workflow, /- name: Build production Angular product/);
  assert.match(workflow, /- name: Test Angular product/);
  assert.match(workflow, /- name: Provision and verify pinned Gitleaks 8\.24\.2/);
  assert.match(workflow, /- name: Run repository security gate/);
});

test('repository pins match the required workflow and preserve the product budget', async () => {
  const [globalJson, nvmrc, angularJson, frontendPackage] = await Promise.all([
    read('global.json'),
    read('.nvmrc'),
    read('frontend/angular.json'),
    read('frontend/package.json'),
  ]);

  assert.equal(JSON.parse(globalJson).sdk.version, '10.0.301');
  assert.equal(nvmrc.trim(), '22.23.1');
  assert.equal(
    JSON.parse(angularJson).projects.frontend.architect.build.configurations.production.budgets[0]
      .maximumError,
    '480kB',
  );
  const scripts = JSON.parse(frontendPackage).scripts;
  assert.equal(scripts['browser:install'], 'node ./node_modules/playwright-core/cli.js install chromium');
  assert.equal(scripts.test, 'node ./tests/run-tests.mjs');
});

test('README publishes the same clean-checkout commands as the required gate', async () => {
  const readme = await read('README.md');

  for (const command of [
    'dotnet restore QualityStudio.slnx --locked-mode',
    'dotnet test QualityStudio.slnx --configuration Release --no-build --filter "Category!=MachineBound"',
    'npm --prefix frontend ci',
    'npm --prefix frontend run browser:install',
    'npm --prefix frontend run build',
    'CHROME_NO_SANDBOX=1 npm --prefix frontend test',
    'security provision',
    'security scan .',
  ]) {
    assert.ok(readme.includes(command), `README is missing required command: ${command}`);
  }
});
