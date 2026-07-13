import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

function findBrowserBinary() {
  const override = process.env.CHROME_BIN;
  if (override && existsSync(override)) {
    return override;
  }

  const candidates = process.platform === 'win32'
    ? [
        'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
        'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
        'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
        'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
      ]
    : process.platform === 'darwin'
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

  return candidates.find((candidate) => existsSync(candidate));
}

const chromeBin = findBrowserBinary();
if (!chromeBin) {
  console.error('Unable to locate a Chrome-compatible browser binary for the Angular test runner.');
  process.exit(1);
}

const ngCli = join(frontendRoot, 'node_modules', '@angular', 'cli', 'bin', 'ng.js');
const result = spawnSync(process.execPath, [ngCli, 'test', '--watch=false', '--browsers=ChromeHeadless'], {
  cwd: frontendRoot,
  env: {
    ...process.env,
    CHROME_BIN: chromeBin,
  },
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
