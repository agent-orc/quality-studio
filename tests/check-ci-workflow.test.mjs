import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const workflowPath = resolve(repoRoot, '.github', 'workflows', 'build.yml');

function parseSteps(yaml) {
  const stepBlocks = yaml.split(/\n(?=      - name: )/);
  return stepBlocks
    .filter((block) => block.trimStart().startsWith('- name: '))
    .map((block) => {
      const name = block.match(/- name: (.+)/)[1].trim();
      return { name, block };
    });
}

test('CI covers the Angular/Node build lane alongside the existing .NET lane', async () => {
  const yaml = await readFile(workflowPath, 'utf8');
  const steps = parseSteps(yaml);
  const byName = (name) => steps.find((step) => step.name === name);

  assert.ok(byName('Set up Node'), 'Node must be provisioned before any npm command runs');
  assert.match(byName('Set up Node').block, /uses: actions\/setup-node@v4/);

  const install = byName('Install frontend dependencies');
  assert.ok(install, 'frontend dependencies must be installed via npm ci');
  assert.match(install.block, /working-directory: frontend/);
  assert.match(install.block, /run: npm ci/);

  const typeCheck = byName('Frontend type check');
  assert.ok(typeCheck, 'tsc must run as the fast compiler pass');
  assert.match(typeCheck.block, /working-directory: frontend/);
  assert.match(typeCheck.block, /tsc --noEmit/);
  assert.doesNotMatch(
    typeCheck.block,
    /continue-on-error/,
    'a compiler error must block the build, not just warn',
  );

  const build = byName('Frontend build (Angular compiler + production budgets)');
  assert.ok(build, 'ng build must run the Angular template compiler and enforce bundle budgets');
  assert.match(build.block, /working-directory: frontend/);
  assert.match(build.block, /ng build --configuration production/);
  assert.doesNotMatch(
    build.block,
    /continue-on-error/,
    'a bundle budget error must block the project performance aggregate, per the dossier disposition table',
  );

  const audit = byName('Frontend dependency audit');
  assert.ok(audit, 'npm audit must run so dependency advisories are visible');
  assert.match(audit.block, /working-directory: frontend/);
  assert.match(audit.block, /run: npm audit/);
  assert.match(
    audit.block,
    /continue-on-error: true/,
    'dependency advisories are standalone findings and must not block CI (dossier section 05)',
  );

  const nodeSetupIndex = steps.findIndex((step) => step.name === 'Set up Node');
  const installIndex = steps.findIndex((step) => step.name === 'Install frontend dependencies');
  const typeCheckIndex = steps.findIndex((step) => step.name === 'Frontend type check');
  const buildIndex = steps.findIndex((step) => step.name === 'Frontend build (Angular compiler + production budgets)');
  assert.ok(nodeSetupIndex < installIndex, 'Node must be set up before npm ci');
  assert.ok(installIndex < typeCheckIndex, 'dependencies must be installed before type checking');
  assert.ok(typeCheckIndex < buildIndex, 'the fast tsc pass should run before the slower ng build');

  const dotnetBuildIndex = steps.findIndex((step) => step.name === 'Build');
  assert.ok(buildIndex < dotnetBuildIndex, 'the Angular lane stays alongside, not after, the existing .NET lane');
});
