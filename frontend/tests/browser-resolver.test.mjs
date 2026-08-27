import test from 'node:test';
import assert from 'node:assert/strict';
import { findBrowserBinary, systemBrowserCandidates } from './browser-resolver.mjs';

test('prefers an explicit CHROME_BIN override when it exists on disk', () => {
  const result = findBrowserBinary({
    env: { CHROME_BIN: '/custom/chrome' },
    platform: 'linux',
    exists: (path) => path === '/custom/chrome',
    resolvePlaywrightChromium: () => {
      throw new Error('should not be called');
    },
  });
  assert.equal(result, '/custom/chrome');
});

test('ignores a CHROME_BIN override that does not exist and falls back to a system candidate', () => {
  const result = findBrowserBinary({
    env: { CHROME_BIN: '/missing/chrome' },
    platform: 'linux',
    exists: (path) => path === '/usr/bin/chromium',
    resolvePlaywrightChromium: () => {
      throw new Error('should not be called');
    },
  });
  assert.equal(result, '/usr/bin/chromium');
});

test('falls back to the first matching system browser candidate for the current platform', () => {
  const candidates = systemBrowserCandidates('linux');
  const result = findBrowserBinary({
    env: {},
    platform: 'linux',
    exists: (path) => path === candidates[candidates.length - 1],
  });
  assert.equal(result, candidates[candidates.length - 1]);
});

test('falls back to the cached Playwright Chromium when no system browser is installed', () => {
  const result = findBrowserBinary({
    env: {},
    platform: 'linux',
    exists: () => false,
    resolvePlaywrightChromium: () => '/home/agent/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome',
  });
  assert.equal(result, '/home/agent/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome');
});

test('returns undefined when neither a system browser nor Playwright Chromium resolves', () => {
  const result = findBrowserBinary({
    env: {},
    platform: 'linux',
    exists: () => false,
    resolvePlaywrightChromium: () => undefined,
  });
  assert.equal(result, undefined);
});
