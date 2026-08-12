import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { portableFilter, trxNotExecutedCount } from '../scripts/pre-review.mjs';

const repositoryRoot = fileURLToPath(new URL('..', import.meta.url));
const readRepositoryFile = path => readFile(join(repositoryRoot, path), 'utf8');

test('portable, machine-bound, and external-live selections cannot drift', async () => {
  const manifest = JSON.parse(await readRepositoryFile('.quality/test-lanes.json'));
  const requiredWorkflow = await readRepositoryFile('.github/workflows/build.yml');
  const canaryWorkflow = await readRepositoryFile('.github/workflows/release-canary.yml');
  const packageDocument = JSON.parse(await readRepositoryFile('package.json'));
  const filter = portableFilter(manifest);

  assert.deepEqual(manifest.portable.excludedCategories, ['MachineBound', 'ExternalLive']);
  assert.equal(manifest.machineBound.category, 'MachineBound');
  assert.equal(manifest.machineBound.minimumSamples, 3);
  assert.equal(manifest.externalLive.category, 'ExternalLive');
  assert.equal(packageDocument.scripts['test:pre-review'], 'node scripts/pre-review.mjs');

  const requiredFilters = [...requiredWorkflow.matchAll(/--filter "([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(requiredFilters, [filter, filter]);
  assert.equal((canaryWorkflow.match(/--filter "Category=MachineBound"/g) ?? []).length, 2);
  assert.equal((canaryWorkflow.match(/--filter "Category=ExternalLive"/g) ?? []).length, 1);
  assert.match(requiredWorkflow, /os: \[ubuntu-latest, windows-latest\]/);
  assert.match(requiredWorkflow, /npm run test:repository-contracts/);

  const reviewRunnerTests = await readRepositoryFile('tests/AgentOrchestrator.CodeQuality.Tests/ReviewRunnerTests.cs');
  assert.match(reviewRunnerTests, /\[Trait\("Category", "ExternalLive"\)\]/);
  assert.match(reviewRunnerTests, /Environment\.GetEnvironmentVariable\("QUALITY_RUN_LIVE_REVIEW"\)/);
});

test('test sources contain no silent skip or focused-test escape hatch', async () => {
  const csharpTests = await collectFiles(join(repositoryRoot, 'tests'), path => path.endsWith('.cs'));
  const javascriptTests = await collectFiles(repositoryRoot, path =>
    path.endsWith('.test.mjs') || path.endsWith('.spec.ts'));

  for (const path of csharpTests) {
    const source = await readFile(path, 'utf8');
    assert.doesNotMatch(source, /Assert\.Skip\s*\(/, `${path} must classify non-portable tests instead of skipping`);
    assert.doesNotMatch(
      source,
      /\[(?:Fact|Theory)\s*\([^\]]*\b(?:Skip|SkipWhen|SkipUnless|Explicit)\s*=/,
      `${path} must not disable or conditionally skip an xUnit test`,
    );
  }

  const disabledNames = ['x' + 'it', 'x' + 'describe', 'f' + 'it', 'f' + 'describe'];
  const disabledCall = new RegExp(`\\b(?:${disabledNames.join('|')})\\s*\\(`);
  const modifierNames = ['sk' + 'ip', 'to' + 'do', 'on' + 'ly'];
  const disabledModifier = new RegExp(`\\.(?:${modifierNames.join('|')})\\s*\\(`);
  for (const path of javascriptTests) {
    const source = await readFile(path, 'utf8');
    assert.doesNotMatch(source, disabledCall, `${path} must not focus or disable a test`);
    assert.doesNotMatch(source, disabledModifier, `${path} must not skip, defer, or focus a test`);
  }
});

test('portable process fixtures stay centralized and operating-system neutral', async () => {
  const csharpTests = await collectFiles(join(repositoryRoot, 'tests'), path => path.endsWith('.cs'));
  const directGitProcesses = [];
  for (const path of csharpTests) {
    const source = await readFile(path, 'utf8');
    if (/ProcessStartInfo\s*\(\s*"git"/.test(source)) directGitProcesses.push(path);
  }
  assert.deepEqual(directGitProcesses.map(path => path.replaceAll('\\', '/').split('/tests/')[1]), [
    'TestSupport/GitTestRepository.cs',
  ]);

  const devStackTests = await readRepositoryFile('tests/dev-stack.test.mjs');
  assert.doesNotMatch(devStackTests, /npm\.cmd/);
  assert.match(devStackTests, /npm-stub\.mjs/);
  assert.match(devStackTests, /createServer\(\)/);
  assert.match(devStackTests, /Captured api output/);

  const gitleaksTests = await readRepositoryFile('tests/AgentOrchestrator.CodeQuality.Tests/GitleaksSecurityScannerTests.cs');
  assert.match(gitleaksTests, /_previousFakeScenario/);
  assert.match(gitleaksTests, /_previousFakeVersion/);
  assert.match(gitleaksTests, /SetScenario\("repository"\);/);
  assert.match(gitleaksTests, /SetEnvironmentVariable\("FAKE_GITLEAKS_SCENARIO", _previousFakeScenario\)/);
  assert.match(gitleaksTests, /SetEnvironmentVariable\("FAKE_GITLEAKS_VERSION", _previousFakeVersion\)/);

  const karmaConfiguration = await readRepositoryFile('frontend/karma.conf.cjs');
  assert.match(karmaConfiguration, /retryLimit:\s*0/);
});

test('pre-review rejects selected tests that did not execute', () => {
  assert.equal(trxNotExecutedCount('<Counters total="4" executed="4" notExecuted="0" />'), 0);
  assert.equal(trxNotExecutedCount('<Counters total="4" executed="3" notExecuted="1" />'), 1);
  assert.throws(() => trxNotExecutedCount('<TestRun />'), /no notExecuted counter/);
});

async function collectFiles(root, include) {
  const files = [];
  async function visit(directory) {
    const entries = await readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.name === 'node_modules' || entry.name === 'dist' || entry.name === 'bin' || entry.name === 'obj') continue;
      const path = join(directory, entry.name);
      if (entry.isDirectory()) await visit(path);
      else if (include(path) && basename(path) !== '') files.push(path);
    }
  }
  await visit(root);
  return files.sort();
}
