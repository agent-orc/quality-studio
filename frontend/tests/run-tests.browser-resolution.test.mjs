import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveBrowserBinary } from './run-tests.mjs';

test('an existing CHROME_BIN override always wins', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: { CHROME_BIN: '/custom/chrome' },
    exists: (path) => path === '/custom/chrome',
    resolvePlaywright: () => '/should-not-be-used',
  });
  assert.equal(result, '/custom/chrome');
});

test('a non-existent CHROME_BIN override is ignored', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: { CHROME_BIN: '/missing/chrome' },
    exists: (path) => path === '/usr/bin/google-chrome',
    resolvePlaywright: () => undefined,
  });
  assert.equal(result, '/usr/bin/google-chrome');
});

test('falls back to a known system path per platform', () => {
  const result = resolveBrowserBinary({
    platform: 'darwin',
    env: {},
    exists: (path) => path === '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    resolvePlaywright: () => undefined,
  });
  assert.equal(result, '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome');
});

test('falls back to the cached Playwright Chromium when no system browser exists', () => {
  const playwrightPath = '/home/example/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome';
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: (path) => path === playwrightPath,
    resolvePlaywright: () => playwrightPath,
  });
  assert.equal(result, playwrightPath);
});

test('returns undefined when neither a system browser nor Playwright Chromium is available', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: () => false,
    resolvePlaywright: () => undefined,
  });
  assert.equal(result, undefined);
});

test('ignores a Playwright resolution that points at a file that does not exist', () => {
  const result = resolveBrowserBinary({
    platform: 'linux',
    env: {},
    exists: () => false,
    resolvePlaywright: () => '/not/actually/there/chrome',
  });
  assert.equal(result, undefined);
});
