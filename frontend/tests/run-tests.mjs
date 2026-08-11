import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { findBrowserBinary } from './browser-binary.mjs';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

const chromeBin = findBrowserBinary();
if (!chromeBin) {
  console.error('Unable to locate Playwright Chromium or a system Chrome-compatible browser. Run `npx playwright install chromium`.');
  process.exit(1);
}

const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const browser = process.env.CHROME_NO_SANDBOX === '1'
  ? 'ChromeHeadlessNoSandbox'
  : 'ChromeHeadless';
const coverageArguments = process.argv.includes('--coverage') ? ['--code-coverage'] : [];
const result = spawnSync(process.execPath, [ngCli, 'test', '--watch=false', `--browsers=${browser}`, ...coverageArguments], {
  cwd: frontendRoot,
  env: {
    ...process.env,
    CHROME_BIN: chromeBin,
    QUALITY_STUDIO_COVERAGE: coverageArguments.length ? '1' : '0',
  },
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
