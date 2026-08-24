import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { chromium } from 'playwright-core';
import { resolveBrowserBinary } from './browser-resolver.mjs';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

const resolution = resolveBrowserBinary({
  platform: process.platform,
  env: process.env,
  exists: existsSync,
  playwrightExecutablePath: () => chromium.executablePath(),
});

if (!resolution.binary) {
  console.error(resolution.reason);
  console.error(`Paths tried:\n  ${resolution.attempted.join('\n  ')}`);
  process.exit(1);
}

console.log(`Angular tests using the ${resolution.source} browser at ${resolution.binary}`);

const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const browser = process.env.CHROME_NO_SANDBOX === '1'
  ? 'ChromeHeadlessNoSandbox'
  : 'ChromeHeadless';
const ngArgs = [ngCli, 'test', '--watch=false', `--browsers=${browser}`];
if (process.argv.includes('--coverage')) {
  ngArgs.push('--code-coverage');
}

const result = spawnSync(process.execPath, ngArgs, {
  cwd: frontendRoot,
  env: {
    ...process.env,
    CHROME_BIN: resolution.binary,
  },
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
