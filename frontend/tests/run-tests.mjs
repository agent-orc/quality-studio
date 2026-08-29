import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

import { BrowserNotFoundError, locateBrowserBinary } from './browser-locator.mjs';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

let browserBinary;
try {
  browserBinary = locateBrowserBinary();
} catch (error) {
  if (error instanceof BrowserNotFoundError) {
    console.error(error.message);
    process.exit(1);
  }
  throw error;
}

console.log(`Angular specs run against ${browserBinary.path} (resolved from ${browserBinary.source}).`);

const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const browser = process.env.CHROME_NO_SANDBOX === '1'
  ? 'ChromeHeadlessNoSandbox'
  : 'ChromeHeadless';
const result = spawnSync(process.execPath, [ngCli, 'test', '--watch=false', `--browsers=${browser}`], {
  cwd: frontendRoot,
  env: {
    ...process.env,
    CHROME_BIN: browserBinary.path,
  },
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
