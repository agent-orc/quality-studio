import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const workflowPath = fileURLToPath(new URL('../.github/workflows/release-canary.yml', import.meta.url));

test('release canary retains every sample before enforcing its classified result', async () => {
  const workflow = await readFile(workflowPath, 'utf8');

  const machineStep = workflow.match(/      - name: Run three machine-bound \.NET samples[\s\S]*?(?=\n      - name:)/)?.[0] ?? '';
  assert.match(machineStep, /id: machine/);
  assert.match(machineStep, /continue-on-error: true/);
  assert.match(machineStep, /for sample in 1 2 3/);
  assert.match(machineStep, /machine-core-\$sample\.trx/);
  assert.match(machineStep, /machine-api-\$sample\.trx/);
  assert.match(machineStep, /\|\| status=1/);

  const browserStep = workflow.match(/      - name: Run three browser performance samples[\s\S]*?(?=\n      - name:)/)?.[0] ?? '';
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
