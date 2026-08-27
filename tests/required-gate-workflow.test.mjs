import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const workflowUrl = new URL('../.github/workflows/build.yml', import.meta.url);
const dotnetPinUrl = new URL('../global.json', import.meta.url);
const nodePinUrl = new URL('../.nvmrc', import.meta.url);

test('required gate pins tools and runs every portable shipping check', async () => {
  const workflow = await readFile(workflowUrl, 'utf8');

  for (const required of [
    'pull_request:',
    'dotnet-version: 10.0.301',
    'node-version: 22.23.1',
    'npm ci',
    'npm run browser:install -- --with-deps',
    'npm run test:browser-resolver',
    'npm run build',
    'npm test',
    '--filter "Category!=MachineBound"',
    '-- security provision',
    '-- security scan .',
  ]) {
    assert.match(workflow, new RegExp(escapeRegExp(required)), `missing required gate command: ${required}`);
  }

  assert.doesNotMatch(workflow, /dotnet-version:\s*10\.0\.x/);
});

test('repository tool pins match the required gate', async () => {
  const [dotnetPin, nodePin] = await Promise.all([
    readFile(dotnetPinUrl, 'utf8'),
    readFile(nodePinUrl, 'utf8'),
  ]);

  assert.deepEqual(JSON.parse(dotnetPin), {
    sdk: { version: '10.0.301', rollForward: 'disable' },
  });
  assert.equal(nodePin.trim(), '22.23.1');
});

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
