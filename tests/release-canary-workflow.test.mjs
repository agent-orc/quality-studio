import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const workflowPath = fileURLToPath(new URL('../.github/workflows/release-canary.yml', import.meta.url));
const browserPerformancePath = fileURLToPath(new URL('../frontend/tests/perf.mjs', import.meta.url));
const realApiJourneyPath = fileURLToPath(new URL('../frontend/tests/project-switch-perf.mjs', import.meta.url));

test('release canary retains every sample before enforcing its classified result', async () => {
  const workflow = await readFile(workflowPath, 'utf8');

  const machineStep = workflow.match(/      - name: Run three machine-bound \.NET samples[\s\S]*?(?=\n      - name:)/)?.[0] ?? '';
  assert.match(machineStep, /id: machine/);
  assert.match(machineStep, /continue-on-error: true/);
  assert.match(machineStep, /for sample in 1 2 3/);
  assert.match(machineStep, /machine-core-\$sample\.trx/);
  assert.match(machineStep, /machine-api-\$sample\.trx/);
  assert.equal((machineStep.match(/\|\| status=1/g) ?? []).length, 2);

  const browserStep = workflow.match(/      - name: Run three real-API browser journey and performance samples[\s\S]*?(?=\n      - name:)/)?.[0] ?? '';
  assert.match(browserStep, /id: browser/);
  assert.match(browserStep, /continue-on-error: true/);
  assert.match(browserStep, /for sample in 1 2 3/);
  assert.match(browserStep, /browser-\$sample/);
  assert.match(browserStep, /project-switch-\$sample/);
  assert.equal((browserStep.match(/\|\| status=1/g) ?? []).length, 2);

  const publishStep = workflow.match(/      - name: Publish canary evidence[\s\S]*?(?=\n      - name:)/)?.[0] ?? '';
  assert.match(publishStep, /if: always\(\)/);
  assert.match(publishStep, /if-no-files-found: error/);

  const resultStep = workflow.match(/      - name: Enforce classified canary result[\s\S]*$/)?.[0] ?? '';
  assert.match(resultStep, /if: always\(\)/);
  assert.match(resultStep, /machine variance:/);
  assert.match(resultStep, /product failed:/);
  assert.match(resultStep, /external dependency unavailable:/);
  assert.match(resultStep, /exit "\$status"/);
});

test('browser performance canary selects the repository root without assuming a checkout name', async () => {
  const harness = await readFile(browserPerformancePath, 'utf8');

  assert.match(harness, /\.tree-row\[aria-level="1"\]/);
  assert.doesNotMatch(harness, /data-node-id="quality-studio"/);
});

test('real-API canary asserts the functional repository-switch journey', async () => {
  const harness = await readFile(realApiJourneyPath, 'utf8');

  assert.match(harness, /real QualityStudio\.Api \(no Playwright response interception\)/);
  assert.match(harness, /\.health\[data-connection-state="live"\]/);
  assert.match(harness, /assertSelectedRepository\(page, 'Small fixture'\)/);
  assert.match(harness, /assertSelectedRepository\(page, name\)/);
  assert.match(harness, /journey\.dashboardHealthCards === 0/);
});
