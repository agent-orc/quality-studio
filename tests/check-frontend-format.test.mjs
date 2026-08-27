import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveChangedFiles } from '../scripts/check-frontend-format.mjs';

test('resolveChangedFiles merges tracked diff and untracked status entries under the watched prefix', () => {
  const diffOutput = 'frontend/src/app/app.ts\nsrc/QualityStudio.Api/Program.cs\n';
  const statusOutput = '?? frontend/src/app/new-widget.ts\n?? README.md\n M frontend/src/app/app.ts\n';

  const files = resolveChangedFiles({ diffOutput, statusOutput, watchedPrefix: 'frontend/' });

  assert.deepEqual(files, ['frontend/src/app/app.ts', 'frontend/src/app/new-widget.ts']);
});

test('resolveChangedFiles filters to prettier-supported extensions only', () => {
  const diffOutput = 'frontend/src/app/app.ts\nfrontend/src/app/app.spec.ts.orig\nfrontend/README.md\n';
  const statusOutput = '';

  const files = resolveChangedFiles({ diffOutput, statusOutput, watchedPrefix: 'frontend/' });

  assert.deepEqual(files, ['frontend/src/app/app.ts']);
});

test('resolveChangedFiles returns nothing when nothing changed', () => {
  const files = resolveChangedFiles({ diffOutput: '', statusOutput: '', watchedPrefix: 'frontend/' });
  assert.deepEqual(files, []);
});

test('resolveChangedFiles ignores tracked-modified status lines (already covered by diff) and dedupes overlapping untracked entries', () => {
  const diffOutput = 'frontend/src/app/app.ts\n';
  const statusOutput = ' M frontend/src/app/app.ts\n?? frontend/src/app/new-widget.ts\n?? frontend/src/app/new-widget.ts\n';

  const files = resolveChangedFiles({ diffOutput, statusOutput, watchedPrefix: 'frontend/' });

  assert.deepEqual(files, ['frontend/src/app/app.ts', 'frontend/src/app/new-widget.ts']);
});
