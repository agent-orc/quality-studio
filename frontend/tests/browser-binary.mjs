import { existsSync } from 'node:fs';
import { chromium } from 'playwright-core';

export function findBrowserBinary({
  override = process.env.CHROME_BIN,
  platform = process.platform,
  exists = existsSync,
  playwrightExecutable = chromium.executablePath(),
} = {}) {
  if (override) {
    if (!exists(override)) {
      throw new Error(`CHROME_BIN does not point to an existing browser: ${override}`);
    }
    return override;
  }

  const systemCandidates = platform === 'win32'
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

  return [playwrightExecutable, ...systemCandidates].find(candidate => candidate && exists(candidate));
}
