import test from 'node:test';
import assert from 'node:assert/strict';
import { findBrowserBinary } from './browser-binary.mjs';

test('uses an explicit browser before provisioned and system candidates', () => {
  const found = findBrowserBinary({
    override: '/tools/chrome',
    exists: candidate => candidate === '/tools/chrome',
    playwrightExecutable: '/cache/chromium',
  });

  assert.equal(found, '/tools/chrome');
});

test('uses the Chromium provisioned by Playwright when no override is set', () => {
  const found = findBrowserBinary({
    override: '',
    platform: 'linux',
    exists: candidate => candidate === '/cache/chromium',
    playwrightExecutable: '/cache/chromium',
  });

  assert.equal(found, '/cache/chromium');
});

test('rejects a misspelled explicit browser path instead of silently falling back', () => {
  assert.throws(
    () => findBrowserBinary({
      override: '/missing/chrome',
      exists: () => false,
      playwrightExecutable: '/cache/chromium',
    }),
    /CHROME_BIN does not point to an existing browser/,
  );
});
