import { existsSync } from 'node:fs';

export function findBrowserBinary({
  environment = process.env,
  platform = process.platform,
  pathExists = existsSync,
  playwrightExecutablePath,
} = {}) {
  const override = environment.CHROME_BIN;
  if (override && pathExists(override)) return override;

  const playwrightPath = safePlaywrightPath(playwrightExecutablePath);
  if (playwrightPath && pathExists(playwrightPath)) return playwrightPath;

  return systemCandidates(platform).find(candidate => pathExists(candidate));
}

function safePlaywrightPath(resolvePath) {
  if (!resolvePath) return undefined;
  try {
    return resolvePath();
  } catch {
    return undefined;
  }
}

function systemCandidates(platform) {
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
