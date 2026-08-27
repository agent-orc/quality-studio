export const RESOLUTION_ORDER = ['CHROME_BIN', 'playwright-core', 'system'];

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

export function resolveBrowserBinary({ platform, env, exists, playwrightExecutablePath }) {
  const attempted = [];
  const override = env.CHROME_BIN;

  if (override) {
    attempted.push(`CHROME_BIN=${override}`);
    if (!exists(override)) {
      return {
        binary: null,
        reason: `CHROME_BIN is set to "${override}", but no file exists there.`,
        attempted,
      };
    }

    return { binary: override, source: 'CHROME_BIN' };
  }

  let playwrightPath = null;
  try {
    playwrightPath = playwrightExecutablePath();
  } catch (error) {
    const message = error instanceof Error ? error.message.split('\n')[0] : String(error);
    attempted.push(`playwright-core (${message})`);
  }

  if (playwrightPath) {
    attempted.push(`playwright-core=${playwrightPath}`);
    if (exists(playwrightPath)) {
      return { binary: playwrightPath, source: 'playwright-core' };
    }
  }

  for (const candidate of systemCandidates(platform)) {
    attempted.push(candidate);
    if (exists(candidate)) {
      return { binary: candidate, source: 'system' };
    }
  }

  return {
    binary: null,
    reason:
      'No Chrome-compatible browser was found. Run "npm run browser:install" to provision the pinned ' +
      'Chromium, or set CHROME_BIN to an existing binary.',
    attempted,
  };
}
