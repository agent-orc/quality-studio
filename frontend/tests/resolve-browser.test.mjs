import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveBrowserBinary } from './resolve-browser.mjs';

test('prefers an explicit CHROME_BIN override that exists on disk', () => {
  const result = resolveBrowserBinary({
    env: { CHROME_BIN: '/opt/custom/chrome' },
    platform: 'linux',
    fileExists: (candidate) => candidate === '/opt/custom/chrome',
    resolvePlaywright: () => assert.fail('should not consult playwright when override is valid'),
  });
  assert.equal(result, '/opt/custom/chrome');
});

test('ignores a CHROME_BIN override that does not exist and falls through', () => {
  const result = resolveBrowserBinary({
    env: { CHROME_BIN: '/missing/chrome' },
    platform: 'linux',
    fileExists: (candidate) => candidate === '/usr/bin/google-chrome',
    resolvePlaywright: () => assert.fail('should stop at the system candidate'),
  });
  assert.equal(result, '/usr/bin/google-chrome');
});

test('falls back to a system browser install when no override is set', () => {
  const result = resolveBrowserBinary({
    env: {},
    platform: 'linux',
    fileExists: (candidate) => candidate === '/usr/bin/chromium',
  });
  assert.equal(result, '/usr/bin/chromium');
});

test('falls back to the cached Playwright Chromium on a clean checkout', () => {
  const result = resolveBrowserBinary({
    env: {},
    platform: 'linux',
    fileExists: (candidate) => candidate === '/home/agent/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome',
    resolvePlaywright: () => '/home/agent/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome',
  });
  assert.equal(result, '/home/agent/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome');
});

test('returns undefined when nothing resolves', () => {
  const result = resolveBrowserBinary({
    env: {},
    platform: 'linux',
    fileExists: () => false,
    resolvePlaywright: () => undefined,
  });
  assert.equal(result, undefined);
});

test('uses darwin candidates on macOS', () => {
  const result = resolveBrowserBinary({
    env: {},
    platform: 'darwin',
    fileExists: (candidate) => candidate === '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    resolvePlaywright: () => undefined,
  });
  assert.equal(result, '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome');
});
