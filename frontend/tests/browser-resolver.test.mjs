import test from 'node:test';
import assert from 'node:assert/strict';
import { RESOLUTION_ORDER, resolveBrowserBinary, systemCandidates } from './browser-resolver.mjs';

const noPlaywright = () => {
  throw new Error("Executable doesn't exist at /cache/chromium-1/chrome");
};

test('documents the deterministic resolution order', () => {
  assert.deepEqual(RESOLUTION_ORDER, ['CHROME_BIN', 'playwright-core', 'system']);
});

test('an explicit CHROME_BIN wins over every other source', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: { CHROME_BIN: '/opt/pinned/chrome' },
    exists: (path) => path === '/opt/pinned/chrome' || path === '/usr/bin/google-chrome',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.deepEqual(result, { binary: '/opt/pinned/chrome', source: 'CHROME_BIN' });
});

test('a missing CHROME_BIN fails loudly instead of using another browser', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: { CHROME_BIN: '/opt/gone/chrome' },
    exists: (path) => path === '/usr/bin/google-chrome',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.equal(result.binary, null);
  assert.match(result.reason, /CHROME_BIN is set to "\/opt\/gone\/chrome"/);
});

test('the cached playwright-core Chromium is preferred over a system install', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: () => true,
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.deepEqual(result, { binary: '/cache/chromium-1234/chrome', source: 'playwright-core' });
});

test('a system install is the fallback when playwright has no cached browser', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: (path) => path === '/usr/bin/chromium',
    playwrightExecutablePath: noPlaywright,
  });

  assert.deepEqual(result, { binary: '/usr/bin/chromium', source: 'system' });
});

test('a missing playwright path falls through to the system list', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: (path) => path === '/usr/bin/google-chrome-stable',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.deepEqual(result, { binary: '/usr/bin/google-chrome-stable', source: 'system' });
});

test('each supported platform gets its own candidate list', () => {
  assert.ok(systemCandidates('win32').every((candidate) => candidate.endsWith('.exe')));
  assert.ok(
    systemCandidates('darwin').every((candidate) => candidate.startsWith('/Applications/')),
  );
  assert.ok(systemCandidates('linux').includes('/usr/bin/chromium'));
  assert.deepEqual(systemCandidates('freebsd'), systemCandidates('linux'));
});

test('an unresolved browser names the provisioning command and attempted paths', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: () => false,
    playwrightExecutablePath: noPlaywright,
  });

  assert.equal(result.binary, null);
  assert.match(result.reason, /npm run browser:install/);
  assert.ok(result.attempted.some((entry) => entry.startsWith('playwright-core (')));
  assert.ok(result.attempted.includes('/usr/bin/google-chrome'));
});
