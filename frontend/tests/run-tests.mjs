import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { findBrowserBinary } from './browser-binary.mjs';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

let chromeBin;
try {
  chromeBin = findBrowserBinary();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}
if (!chromeBin) {
  console.error('Unable to locate a Chrome-compatible browser. Run `npx playwright install chromium` in frontend/.');
  process.exit(1);
}

const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const browser = process.env.CHROME_NO_SANDBOX === '1'
  ? 'ChromeHeadlessNoSandbox'
  : 'ChromeHeadless';
const coverageArgs = process.argv.includes('--coverage') ? ['--code-coverage'] : [];
const result = spawnSync(process.execPath, [ngCli, 'test', '--watch=false', `--browsers=${browser}`, ...coverageArgs], {
  cwd: frontendRoot,
  env: {
    ...process.env,
    CHROME_BIN: chromeBin,
  },
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
