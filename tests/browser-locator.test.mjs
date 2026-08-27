import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  BrowserNotFoundError,
  locateBrowserBinary,
  resolvePlaywrightChromium,
  systemCandidates,
} from '../frontend/tests/browser-locator.mjs';

const PINNED = '/cache/ms-playwright/chromium-1228/chrome-linux64/chrome';

/** Build the injected dependencies for a host where only `present` paths exist. */
function host({ env = {}, platform = 'linux', present = [], pinned = undefined } = {}) {
  const existing = new Set(present);
  return {
    env,
    platform,
    fileExists: (candidate) => existing.has(candidate),
    playwrightChromium: () => pinned,
  };
}

test('CHROME_BIN wins when it points at an existing binary', () => {
  const resolved = locateBrowserBinary(
    host({ env: { CHROME_BIN: '/opt/chrome' }, present: ['/opt/chrome', PINNED], pinned: PINNED }),
  );

  assert.deepEqual(resolved, { path: '/opt/chrome', source: 'CHROME_BIN' });
});

test('a CHROME_BIN that does not exist fails loudly instead of falling back', () => {
  assert.throws(
    () =>
      locateBrowserBinary(
        host({
          env: { CHROME_BIN: '/opt/missing-chrome' },
          present: ['/usr/bin/google-chrome', PINNED],
          pinned: PINNED,
        }),
      ),
    (error) => {
      assert.ok(error instanceof BrowserNotFoundError);
      assert.match(error.message, /\/opt\/missing-chrome/);
      return true;
    },
    'an explicit override that is wrong must not be silently replaced by a system browser',
  );
});

test('the pinned Playwright Chromium is preferred over an ad-hoc system install', () => {
  const resolved = locateBrowserBinary(
    host({ present: ['/usr/bin/google-chrome', PINNED], pinned: PINNED }),
  );

  assert.deepEqual(resolved, { path: PINNED, source: 'playwright-core' });
});

test('a system browser is used when the pinned Chromium is not provisioned', () => {
  const resolved = locateBrowserBinary(
    host({ present: ['/usr/bin/chromium'], pinned: PINNED }),
  );

  assert.deepEqual(resolved, { path: '/usr/bin/chromium', source: 'system' });
});

test('a system browser is used when playwright-core is absent altogether', () => {
  const resolved = locateBrowserBinary(
    host({ present: ['/snap/bin/chromium'], pinned: undefined }),
  );

  assert.deepEqual(resolved, { path: '/snap/bin/chromium', source: 'system' });
});

test('an unprovisioned host names the provisioning command', () => {
  assert.throws(
    () => locateBrowserBinary(host({ present: [], pinned: PINNED })),
    (error) => {
      assert.ok(error instanceof BrowserNotFoundError);
      assert.match(error.message, /browser:install/);
      return true;
    },
  );
});

test('Windows and macOS candidates are offered on their own platforms', () => {
  assert.ok(
    systemCandidates('win32').every((candidate) => candidate.endsWith('.exe')),
    'Windows candidates must be Windows executables',
  );
  assert.ok(systemCandidates('darwin').every((candidate) => candidate.startsWith('/Applications/')));
  assert.deepEqual(systemCandidates('freebsd'), systemCandidates('linux'), 'unknown platforms fall back to the Linux list');
});

test('resolvePlaywrightChromium reports undefined rather than throwing when the package is missing', () => {
  assert.equal(resolvePlaywrightChromium('/nonexistent-root'), undefined);
});

test('resolvePlaywrightChromium resolves the Chromium pinned by frontend/package-lock.json', (t) => {
  const frontendRoot = fileURLToPath(new URL('../frontend/', import.meta.url));
  if (!existsSync(join(frontendRoot, 'node_modules', 'playwright-core'))) {
    t.skip('frontend dependencies are not installed; run `npm --prefix frontend ci` first');
    return;
  }

  const resolved = resolvePlaywrightChromium();

  assert.ok(resolved, 'playwright-core must resolve once frontend dependencies are installed');
  assert.match(resolved, /chromium/, 'the resolved binary must come from a Chromium build');
});
