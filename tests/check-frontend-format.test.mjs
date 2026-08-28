import test from 'node:test';
import assert from 'node:assert/strict';
import { changedFiles } from '../scripts/check-frontend-format.mjs';

function stubGit({ diff = '', untracked = '' } = {}) {
  return (args) => (args[0] === 'diff' ? diff : untracked);
}

test('changedFiles keeps only frontend/src and frontend/tests files with a formattable extension', () => {
  const runner = stubGit({
    diff: [
      'frontend/src/app/app.ts',
      'frontend/src/app/app.spec.ts',
      'src/QualityStudio.Api/Program.cs',
      'docs/operations/static-analysis/index.html',
      'frontend/tests/perf.mjs',
    ].join('\n'),
  });

  const files = changedFiles('origin/main', { runner });
  assert.deepEqual(files, [
    'frontend/src/app/app.spec.ts',
    'frontend/src/app/app.ts',
    'frontend/tests/perf.mjs',
  ]);
});

test('changedFiles includes untracked new files under the tracked directories', () => {
  const runner = stubGit({
    diff: 'frontend/src/app/app.ts',
    untracked: 'frontend/src/app/new-widget.ts\nfrontend/dist/ignored.js',
  });

  const files = changedFiles('origin/main', { runner });
  assert.deepEqual(files, ['frontend/src/app/app.ts', 'frontend/src/app/new-widget.ts']);
});

test('changedFiles dedupes a file that is both tracked-changed and reported untracked', () => {
  const runner = stubGit({
    diff: 'frontend/src/app/app.ts',
    untracked: 'frontend/src/app/app.ts',
  });

  const files = changedFiles('origin/main', { runner });
  assert.deepEqual(files, ['frontend/src/app/app.ts']);
});

test('changedFiles returns nothing when there is no relevant diff', () => {
  const runner = stubGit({ diff: '', untracked: '' });
  assert.deepEqual(changedFiles('origin/main', { runner }), []);
});
