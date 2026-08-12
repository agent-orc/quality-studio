import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
const checks = [
  ['Repository test and gate contracts', process.execPath, ['--test',
    'tests/check-coverage.test.mjs',
    'tests/release-canary-workflow.test.mjs',
    'tests/test-lanes.test.mjs']],
  ['Release compile', 'dotnet', ['build', 'QualityStudio.slnx', '--configuration', 'Release']],
  ['Portable .NET lane', process.execPath, ['scripts/run-dotnet-lane.mjs', 'portable', '--no-build']],
  ['Browser prerequisite resolver', process.execPath, ['--test', 'frontend/tests/browser-binary.test.mjs']],
];

console.log('Fast pre-review builds Release and checks deterministic, non-tool-bound code paths only.');
console.log('The required gate still owns tool-bound, host-integration, production browser, coverage, and security evidence.');

for (const [name, command, args] of checks) {
  const started = Date.now();
  console.log(`\n[pre-review] ${name}`);
  await run(command, args);
  console.log(`[pre-review] ${name} passed in ${((Date.now() - started) / 1000).toFixed(1)}s`);
}

function run(command, args) {
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, args, {
      cwd: repoRoot,
      env: process.env,
      stdio: 'inherit',
      windowsHide: true,
    });
    child.once('error', rejectPromise);
    child.once('exit', code => code === 0
      ? resolvePromise()
      : rejectPromise(new Error(`${command} exited ${code}.`)));
  });
}
