import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveBrowserBinary, systemCandidates } from './browser-resolver.mjs';

const missing = () => false;
const noPlaywright = () => {
  throw new Error("Executable doesn't exist at /cache/chromium-1/chrome");
};

test('an explicit CHROME_BIN wins over every other source', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: { CHROME_BIN: '/opt/pinned/chrome' },
    exists: path => path === '/opt/pinned/chrome' || path === '/usr/bin/google-chrome',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.deepEqual(result, { binary: '/opt/pinned/chrome', source: 'CHROME_BIN' });
});

test('a CHROME_BIN that does not exist fails loudly instead of silently using another browser', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: { CHROME_BIN: '/opt/gone/chrome' },
    exists: path => path === '/usr/bin/google-chrome',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.equal(result.binary, null);
  assert.match(result.reason, /CHROME_BIN is set to "\/opt\/gone\/chrome"/);
});

test('the cached playwright-core Chromium is used when no override is set', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: path => path === '/cache/chromium-1234/chrome',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.deepEqual(result, { binary: '/cache/chromium-1234/chrome', source: 'playwright-core' });
});

test('the pinned browser is preferred over a system install', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: () => true,
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.equal(result.source, 'playwright-core');
});

test('a system install is the fallback when playwright reports no cached browser', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: path => path === '/usr/bin/chromium',
    playwrightExecutablePath: noPlaywright,
  });

  assert.deepEqual(result, { binary: '/usr/bin/chromium', source: 'system' });
});

test('a playwright path that points at an uninstalled browser falls through to the system list', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: path => path === '/usr/bin/google-chrome-stable',
    playwrightExecutablePath: () => '/cache/chromium-1234/chrome',
  });

  assert.deepEqual(result, { binary: '/usr/bin/google-chrome-stable', source: 'system' });
});

test('each platform gets its own candidate list', () => {
  assert.ok(systemCandidates('win32').every(candidate => candidate.endsWith('.exe')));
  assert.ok(systemCandidates('darwin').every(candidate => candidate.startsWith('/Applications/')));
  assert.ok(systemCandidates('linux').includes('/usr/bin/chromium'));
  assert.deepEqual(systemCandidates('freebsd'), systemCandidates('linux'));
});

test('an unresolvable browser names the provisioning command and every path it tried', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: missing,
    playwrightExecutablePath: noPlaywright,
  });

  assert.equal(result.binary, null);
  assert.match(result.reason, /npm run browser:install/);
  assert.ok(result.attempted.some(entry => entry.startsWith('playwright-core (')));
  assert.ok(result.attempted.includes('/usr/bin/google-chrome'));
});
