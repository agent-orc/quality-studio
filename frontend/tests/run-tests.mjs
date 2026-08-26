import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { chromium } from 'playwright-core';
import { findBrowserBinary } from './browser-binary.mjs';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

const chromeBin = findBrowserBinary({ playwrightExecutablePath: () => chromium.executablePath() });
if (!chromeBin) {
  console.error('Unable to locate a Chrome-compatible browser. Run `npm run browser:install` or set CHROME_BIN.');
  process.exit(1);
}

const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const browser = process.env.CHROME_NO_SANDBOX === '1'
  ? 'ChromeHeadlessNoSandbox'
  : 'ChromeHeadless';
const coverage = process.argv.includes('--coverage');
const cliArguments = [ngCli, 'test', '--watch=false', `--browsers=${browser}`];
if (coverage) cliArguments.push('--code-coverage');
const result = spawnSync(process.execPath, cliArguments, {
  cwd: frontendRoot,
  env: {
    ...process.env,
    CHROME_BIN: chromeBin,
  },
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
