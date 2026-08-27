import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');
const require = createRequire(import.meta.url);

function resolveCachedPlaywrightChromium() {
  try {
    return require('playwright-core').chromium.executablePath();
  } catch {
    return undefined;
  }
}

// Exported for unit testing: the system-path and Playwright lookups are injected so
// resolution logic can be verified without depending on the host's installed browsers.
export function resolveBrowserBinary({
  platform = process.platform,
  env = process.env,
  exists = existsSync,
  resolvePlaywright = resolveCachedPlaywrightChromium,
} = {}) {
  const override = env.CHROME_BIN;
  if (override && exists(override)) {
    return override;
  }

  const candidates = platform === 'win32'
    ? [
        'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
        'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
        'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
        'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
      ]
    : platform === 'darwin'
      ? [
          '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
          '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
        ]
      : [
          '/usr/bin/google-chrome',
          '/usr/bin/google-chrome-stable',
          '/usr/bin/chromium',
          '/usr/bin/chromium-browser',
          '/snap/bin/chromium',
        ];

  const systemMatch = candidates.find((candidate) => exists(candidate));
  if (systemMatch) {
    return systemMatch;
  }

  // The repository depends on playwright-core; fall back to its cached Chromium
  // (provisioned via `npm run browser:install`) instead of requiring a system browser.
  const playwrightMatch = resolvePlaywright();
  return playwrightMatch && exists(playwrightMatch) ? playwrightMatch : undefined;
}

function isMainModule() {
  return process.argv[1] && import.meta.url === `file://${process.argv[1]}`;
}

if (isMainModule()) {
  const chromeBin = resolveBrowserBinary();
  if (!chromeBin) {
    console.error(
      'Unable to locate a Chrome-compatible browser binary for the Angular test runner. ' +
      'Run "npm run browser:install" to provision a pinned Chromium, or set CHROME_BIN.',
    );
    process.exit(1);
  }

  const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
  const browser = process.env.CHROME_NO_SANDBOX === '1'
    ? 'ChromeHeadlessNoSandbox'
    : 'ChromeHeadless';
  const result = spawnSync(process.execPath, [ngCli, 'test', '--watch=false', `--browsers=${browser}`], {
    cwd: frontendRoot,
    env: {
      ...process.env,
      CHROME_BIN: chromeBin,
    },
    stdio: 'inherit',
  });

  process.exit(result.status ?? 1);
}
