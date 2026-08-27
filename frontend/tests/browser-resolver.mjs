import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';

export function systemBrowserCandidates(platform) {
  if (platform === 'win32') {
    return [
      'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
      'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
      'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
      'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    ];
  }
  if (platform === 'darwin') {
    return [
      '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
      '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    ];
  }
  return [
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/snap/bin/chromium',
  ];
}

export function resolvePlaywrightChromiumPath() {
  try {
    const require = createRequire(import.meta.url);
    const { chromium } = require('playwright-core');
    const executablePath = chromium.executablePath();
    return existsSync(executablePath) ? executablePath : undefined;
  } catch {
    return undefined;
  }
}

export function findBrowserBinary({
  env = process.env,
  platform = process.platform,
  exists = existsSync,
  resolvePlaywrightChromium = resolvePlaywrightChromiumPath,
} = {}) {
  const override = env.CHROME_BIN;
  if (override && exists(override)) {
    return override;
  }

  const systemBinary = systemBrowserCandidates(platform).find((candidate) => exists(candidate));
  if (systemBinary) {
    return systemBinary;
  }

  return resolvePlaywrightChromium();
}
