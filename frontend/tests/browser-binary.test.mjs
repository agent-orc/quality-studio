import assert from 'node:assert/strict';
import test from 'node:test';
import { browserCandidates, findBrowserBinary } from './browser-binary.mjs';

test('browser discovery prioritizes an explicit override and de-duplicates candidates', () => {
  const candidates = browserCandidates('linux', { CHROME_BIN: '/custom/chrome' });

  assert.equal(candidates[0], '/custom/chrome');
  assert.equal(new Set(candidates).size, candidates.length);
});

test('browser discovery returns the first existing candidate', () => {
  const found = findBrowserBinary({
    candidates: ['/missing/chrome', '/playwright/chrome', '/system/chrome'],
    exists: (candidate) => candidate !== '/missing/chrome',
  });

  assert.equal(found, '/playwright/chrome');
});
