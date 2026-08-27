import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const repositoryRoot = new URL('../', import.meta.url);

async function read(relativePath) {
  return readFile(new URL(relativePath, repositoryRoot), 'utf8');
}

test('required gate pins tools and covers every portable shipping check', async () => {
  const [workflow, globalJson, frontendPackage, angularJson] = await Promise.all([
    read('.github/workflows/build.yml'),
    read('global.json'),
    read('frontend/package.json'),
    read('frontend/angular.json'),
  ]);
  const sdk = JSON.parse(globalJson);
  const frontend = JSON.parse(frontendPackage);
  const angular = JSON.parse(angularJson);

  assert.equal(sdk.sdk.version, '10.0.301');
  assert.equal(sdk.sdk.rollForward, 'disable');
  assert.equal(frontend.engines.node, '22.x');
  assert.equal(frontend.engines.npm, '10.x');
  assert.equal(
    angular.projects.frontend.architect.build.configurations.production.budgets[0].maximumError,
    '480kB',
  );

  const requiredFragments = [
    'pull_request:',
    'dotnet-version: 10.0.301',
    'node-version: 22.23.1',
    '--filter "Category!=MachineBound"',
    'run: npm ci',
    'run: npm run browser:install -- --with-deps',
    'run: npm run build',
    'run: npm test',
    '-- security provision',
    '-- security scan .',
  ];
  for (const fragment of requiredFragments) {
    assert.ok(workflow.includes(fragment), `required workflow fragment is missing: ${fragment}`);
  }

  const stepNames = [...workflow.matchAll(/^\s+- name: (.+)$/gm)].map((match) => match[1]);
  assert.equal(new Set(stepNames).size, stepNames.length, 'every gate step must have a distinct name');
});
