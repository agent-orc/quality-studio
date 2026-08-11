import test from 'node:test';
import assert from 'node:assert/strict';
import { findBrowserBinary } from './browser-binary.mjs';

test('explicit CHROME_BIN takes precedence over Playwright and system browsers', () => {
  const existing = new Set(['/override/chrome', '/playwright/chrome', '/usr/bin/chromium']);
  const result = findBrowserBinary({
    environment: { CHROME_BIN: '/override/chrome' },
    platform: 'linux',
    pathExists: path => existing.has(path),
    playwrightExecutablePath: () => '/playwright/chrome',
  });
  assert.equal(result, '/override/chrome');
});

test('Playwright Chromium is discovered before a system installation', () => {
  const existing = new Set(['/playwright/chrome', '/usr/bin/chromium']);
  const result = findBrowserBinary({
    environment: {},
    platform: 'linux',
    pathExists: path => existing.has(path),
    playwrightExecutablePath: () => '/playwright/chrome',
  });
  assert.equal(result, '/playwright/chrome');
});

test('resolver falls back to the platform browser list when Playwright is absent', () => {
  const result = findBrowserBinary({
    environment: {},
    platform: 'win32',
    pathExists: path => path.endsWith('Microsoft\\Edge\\Application\\msedge.exe'),
    playwrightExecutablePath: () => { throw new Error('browser is not installed'); },
  });
  assert.equal(result, 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe');
});

test('resolver reports no browser when every declared source is absent', () => {
  const result = findBrowserBinary({
    environment: { CHROME_BIN: '/missing/chrome' },
    platform: 'linux',
    pathExists: () => false,
    playwrightExecutablePath: () => '/missing/playwright',
  });
  assert.equal(result, undefined);
});
