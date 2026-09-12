import test from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { extname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dotnetProjects, testLanes } from '../scripts/test-lanes.mjs';

const repoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));

test('lane filters are mutually explicit and keep live checks out of required runs', () => {
  assert.equal(testLanes.portable.filter,
    'Category!=ToolBound&Category!=MachineBound&Category!=ExternalLive');
  assert.equal(testLanes['tool-bound'].filter,
    'Category=ToolBound&Category!=MachineBound&Category!=ExternalLive');
  assert.equal(testLanes['non-machine'].filter,
    'Category!=MachineBound&Category!=ExternalLive');
  assert.equal(testLanes.machine.filter, 'Category=MachineBound');
  assert.equal(testLanes['external-live'].filter, 'Category=ExternalLive');
  assert.deepEqual(testLanes['external-live'].projects, ['core']);
  assert.deepEqual(dotnetProjects.map(project => project.id), ['core', 'api']);
});

test('required and canary workflows select named lanes and retain all-lane evidence', async () => {
  const required = await readFile(resolve(repoRoot, '.github/workflows/build.yml'), 'utf8');
  assert.match(required, /run-dotnet-lane\.mjs portable\s+--configuration Release --no-build/);
  assert.match(required, /run-dotnet-lane\.mjs tool-bound\s+--configuration Release --no-build/);
  assert.match(required, /run-dotnet-lane\.mjs non-machine --configuration Release --no-build --coverage/);
  assert.match(required, /npm run test:repository-contracts/);

  const canary = await readFile(resolve(repoRoot, '.github/workflows/release-canary.yml'), 'utf8');
  assert.equal((canary.match(/--filter "Category=MachineBound"/g) ?? []).length, 2);
  assert.match(canary, /--filter "Category=ExternalLive"/);
});

test('real process fixtures are centralized and every consumer declares ToolBound', async () => {
  const files = await sourceFiles(resolve(repoRoot, 'backend/tests'));
  assert.ok(files.some(file => file.endsWith('.cs')), 'The backend fixture contract must inspect C# test sources.');
  const gitHelper = resolve(repoRoot, 'backend/tests/TestSupport/GitTestRepository.cs');
  const violations = [];
  for (const file of files.filter(file => extname(file) === '.cs')) {
    const source = await readFile(file, 'utf8');
    const consumesGitFixture = file !== gitHelper && source.includes('GitTestRepository.');
    const ownsRawProcess = file !== gitHelper && /new Process\b|ProcessStartInfo\s*\(/.test(source);
    if ((consumesGitFixture || ownsRawProcess) && !hasClassCategory(source, 'ToolBound')) {
      violations.push(file.slice(repoRoot.length + 1));
    }
    for (const skip of unguardedSkips(source)) {
      assert.fail(`${file.slice(repoRoot.length + 1)} skips without a platform guard: ${skip}. `
        + 'Classify the boundary with a Category instead of skipping it.');
    }
  }
  assert.deepEqual(violations, []);
});

test('dev-stack tests consume the shared platform-neutral process fixture', async () => {
  const source = await readFile(resolve(repoRoot, 'tests/dev-stack.test.mjs'), 'utf8');
  assert.match(source, /from '\.\/TestSupport\/node-process-fixture\.mjs'/);
  assert.doesNotMatch(source, /function npmInstallStub|function npmStubEnvironment|function freePorts/);
});

test('pre-review builds once and runs only the portable lane without rebuilding', async () => {
  const packageDocument = JSON.parse(await readFile(resolve(repoRoot, 'package.json'), 'utf8'));
  assert.equal(packageDocument.scripts['test:pre-review'], 'node scripts/run-pre-review.mjs');

  const source = await readFile(resolve(repoRoot, 'scripts/run-pre-review.mjs'), 'utf8');
  assert.match(source, /'dotnet', \['build', 'QualityStudio\.slnx', '--configuration', 'Release'\]/);
  assert.match(source, /'scripts\/run-dotnet-lane\.mjs', 'portable', '--no-build'/);
  assert.doesNotMatch(source, /scripts\/run-dotnet-lane\.mjs', '(?:tool-bound|machine|external-live)'/);
});

// A missing tool, an absent opt-in, or an offline service must fail the lane it belongs to.
// The single legitimate skip is a capability the operating system cannot exhibit at all, so
// a skip is only accepted directly under an `OperatingSystem.Is...()` guard.
function unguardedSkips(source) {
  const lines = source.split(/\r?\n/);
  return lines
    .map((line, index) => ({ line, previous: lines[index - 1] ?? '' }))
    .filter(({ line }) => /Assert\.Skip\s*\(/.test(line))
    .filter(({ line, previous }) => !/if\s*\(\s*!?\s*OperatingSystem\.Is\w+\(\)\s*\)/.test(previous)
      && !/if\s*\(\s*!?\s*OperatingSystem\.Is\w+\(\)\s*\)/.test(line))
    .map(({ line }) => line.trim());
}

function hasClassCategory(source, category) {
  const escaped = category.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`\\[Trait\\("Category", "${escaped}"\\)\\]\\s*(?:public )?(?:sealed )?class`).test(source);
}

async function sourceFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory() && ['bin', 'obj', 'TestResults'].includes(entry.name)) continue;
    if (entry.isDirectory()) files.push(...await sourceFiles(path));
    else files.push(path);
  }
  return files;
}
