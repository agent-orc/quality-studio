import test from 'node:test';
import assert from 'node:assert/strict';
import { browserCandidates, findBrowserBinary } from './browser-binary.mjs';

test('browser lookup prefers an explicit override and includes Playwright Chromium', () => {
  const candidates = browserCandidates('linux', { CHROME_BIN: '/explicit/chrome' });

  assert.equal(candidates[0], '/explicit/chrome');
  assert.ok(candidates.some(candidate => candidate.includes('chromium')));
});

test('browser lookup returns the first installed candidate', () => {
  const found = findBrowserBinary({
    candidates: ['/missing', '/playwright/chrome', '/system/chrome'],
    exists: candidate => candidate !== '/missing',
  });

  assert.equal(found, '/playwright/chrome');
});
