import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';

const require = createRequire(import.meta.url);

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

export function resolvePlaywrightChromium() {
  try {
    const { chromium } = require('playwright-core');
    return chromium.executablePath();
  } catch {
    return undefined;
  }
}

/**
 * Resolves a Chrome-compatible browser binary. Order: an explicit CHROME_BIN
 * override, a system install, then the Playwright Chromium cached for this
 * repository's pinned playwright-core version. The Playwright fallback is
 * what lets a clean checkout pass without a system browser install.
 */
export function resolveBrowserBinary({
  env = process.env,
  platform = process.platform,
  fileExists = existsSync,
  resolvePlaywright = resolvePlaywrightChromium,
} = {}) {
  const override = env.CHROME_BIN;
  if (override && fileExists(override)) {
    return override;
  }

  const systemCandidates = SYSTEM_CANDIDATES[platform] ?? SYSTEM_CANDIDATES.linux;
  const systemMatch = systemCandidates.find((candidate) => fileExists(candidate));
  if (systemMatch) {
    return systemMatch;
  }

  const playwrightPath = resolvePlaywright();
  if (playwrightPath && fileExists(playwrightPath)) {
    return playwrightPath;
  }

  return undefined;
}
