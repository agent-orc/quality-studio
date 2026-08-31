import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const testsDir = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(testsDir, '..');

export class BrowserNotFoundError extends Error {
  constructor(message) {
    super(message);
    this.name = 'BrowserNotFoundError';
  }
}

const SYSTEM_CANDIDATES = {
  win32: [
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  ],
  darwin: [
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
  ],
  linux: [
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/snap/bin/chromium',
  ],
};

export function systemCandidates(platform) {
  return SYSTEM_CANDIDATES[platform] ?? SYSTEM_CANDIDATES.linux;
}

export function resolvePlaywrightChromium(root = frontendRoot) {
  try {
    const requireFromFrontend = createRequire(join(root, 'package.json'));
    const { chromium } = requireFromFrontend('playwright-core');
    return chromium.executablePath();
  } catch {
    return undefined;
  }
}

export function locateBrowserBinary({
  env = process.env,
  platform = process.platform,
  fileExists = existsSync,
  playwrightChromium = resolvePlaywrightChromium,
} = {}) {
  const override = env.CHROME_BIN;
  if (override) {
    if (!fileExists(override)) {
      throw new BrowserNotFoundError(
        `CHROME_BIN is set to '${override}', but no file exists there. ` +
          'Unset CHROME_BIN to use the pinned Chromium, or point it at a real binary.',
      );
    }
    return { path: override, source: 'CHROME_BIN' };
  }

  const pinned = playwrightChromium();
  if (pinned && fileExists(pinned)) {
    return { path: pinned, source: 'playwright-core' };
  }

  const system = systemCandidates(platform).find((candidate) => fileExists(candidate));
  if (system) {
    return { path: system, source: 'system' };
  }

  throw new BrowserNotFoundError(
    'Unable to locate a Chrome-compatible browser binary for the Angular test runner. ' +
      'Run `npm --prefix frontend run browser:install` to provision the pinned Chromium, ' +
      'or set CHROME_BIN to an existing Chrome, Chromium, or Edge binary.',
  );
}
