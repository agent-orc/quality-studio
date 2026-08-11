import { existsSync } from 'node:fs';
import { chromium } from 'playwright-core';

export function browserCandidates(platform = process.platform, environment = process.env) {
  const candidates = [];
  if (environment.CHROME_BIN) candidates.push(environment.CHROME_BIN);
  candidates.push(chromium.executablePath());

  if (platform === 'win32') {
    candidates.push(
      'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
      'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
      'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
      'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    );
  } else if (platform === 'darwin') {
    candidates.push(
      '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
      '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    );
  } else {
    candidates.push(
      '/usr/bin/google-chrome',
      '/usr/bin/google-chrome-stable',
      '/usr/bin/chromium',
      '/usr/bin/chromium-browser',
      '/snap/bin/chromium',
    );
  }

  return [...new Set(candidates.filter(Boolean))];
}

export function findBrowserBinary(options = {}) {
  const candidates = options.candidates ?? browserCandidates(options.platform, options.environment);
  const fileExists = options.exists ?? existsSync;
  return candidates.find(candidate => fileExists(candidate));
}
