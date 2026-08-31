import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/**
 * The QS-W5 baseline turned red partly because the required gate quietly drifted:
 * the pull-request trigger was removed and the toolchain floated. These are
 * contract assertions over .github/workflows/build.yml, not a YAML semantics
 * test - they pin the properties the dossier's "definition of green" depends on.
 */

const workflowPath = fileURLToPath(new URL('../.github/workflows/build.yml', import.meta.url));
const workflow = readFileSync(workflowPath, 'utf8');

/** Top-level keys of the `on:` mapping, e.g. pull_request / push / workflow_dispatch. */
function triggers(text) {
  const lines = text.split('\n');
  const start = lines.indexOf('on:');
  assert.notEqual(start, -1, 'build.yml must declare an `on:` block');

  const found = [];
  for (const line of lines.slice(start + 1)) {
    if (line.trim() === '') continue;
    if (!line.startsWith('  ')) break; // dedent ends the `on:` block
    const match = /^ {2}([a-z_]+):/.exec(line);
    if (match) found.push(match[1]);
  }
  return found;
}

/** Every `- name:` under a `steps:` list, in declaration order. */
function stepNames(text) {
  return [...text.matchAll(/^ {6}- name: (.+)$/gm)].map((match) => match[1].trim());
}

test('the gate runs on pull requests to main, not only on push', () => {
  const on = triggers(workflow);

  assert.ok(
    on.includes('pull_request'),
    'branch protection cannot require a gate that never runs on pull requests',
  );
  assert.ok(on.includes('push'), 'main must stay covered after merge');
});

test('the toolchain is pinned rather than floating', () => {
  assert.match(workflow, /DOTNET_VERSION: 10\.0\.301/, '.NET must be pinned to the measured SDK');
  assert.match(workflow, /NODE_VERSION: 22\.23\.1/, 'Node must be pinned');
  assert.match(workflow, /GITLEAKS_VERSION: 8\.24\.2/, 'Gitleaks must stay on the pinned version');
  assert.doesNotMatch(
    workflow,
    /dotnet-version: *\d+\.\d+\.x/,
    'a floating dotnet-version lets the gate drift away from what contributors run',
  );
});

test('the gate covers every surface the README claims is covered', () => {
  const steps = stepNames(workflow);

  for (const required of [
    'Install frontend dependencies',
    'Build production frontend bundle',
    'Provision pinned Chromium',
    'Run Angular specs',
    'Restore .NET solution',
    'Build .NET solution',
    'Run portable .NET tests',
    'Provision pinned Gitleaks',
    'Run security scan',
  ]) {
    assert.ok(steps.includes(required), `build.yml is missing a "${required}" step`);
  }

  assert.equal(
    new Set(steps).size,
    steps.length,
    'every step needs a distinct name so its exit status is attributable',
  );
});

test('machine-bound timing checks stay out of the portable lane', () => {
  assert.match(
    workflow,
    /--filter "Category!=MachineBound"/,
    'host timing noise must not be able to fail the required gate',
  );
});

test('the frontend is built in production configuration', () => {
  assert.match(
    workflow,
    /npm --prefix frontend run build/,
    'the 480 kB budget only applies to the production build',
  );
  assert.doesNotMatch(
    workflow,
    /--configuration development/,
    'a development build bypasses the bundle budget and is not shipping evidence',
  );
});

test('external tooling is provisioned before the step that consumes it', () => {
  const steps = stepNames(workflow);

  assert.ok(
    steps.indexOf('Provision pinned Chromium') < steps.indexOf('Run Angular specs'),
    'Chromium must be provisioned before the specs that need it',
  );
  assert.ok(
    steps.indexOf('Provision pinned Gitleaks') < steps.indexOf('Run security scan'),
    'Gitleaks must be provisioned before the scan, so a failed download is not read as a product failure',
  );
  assert.ok(
    steps.indexOf('Install frontend dependencies') < steps.indexOf('Build production frontend bundle'),
    'the production build needs frontend dependencies',
  );
  assert.match(
    workflow,
    /QUALITY_GITLEAKS_PATH=/,
    'the provisioned binary must be handed to the scanner explicitly',
  );
  assert.match(
    workflow,
    /sha256sum --check/,
    'a downloaded scanner binary must be checksum-verified',
  );
});

test('no workflow restores with --locked-mode while the repository has no NuGet lock files', () => {
  // Directory.Build.props does not enable RestorePackagesWithLockFile and no
  // packages.lock.json is committed, so --locked-mode fails on a clean checkout.
  assert.doesNotMatch(
    workflow,
    /--locked-mode/,
    'the required gate must not restore with --locked-mode until lock files are committed',
  );
});
