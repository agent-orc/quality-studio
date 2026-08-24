// Resolves the browser binary the Angular Karma run needs.
//
// The repository already depends on playwright-core, so a pinned Chromium is the
// reproducible answer: a required job provisions exactly one version and every run
// uses it. System installs are only a convenience fallback for developers, and are
// deliberately ranked below the pinned browser so a stale local Chrome cannot change
// a result that CI produced with the pinned one.

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

/**
 * @param {object} options
 * @param {string} options.platform            `process.platform`
 * @param {Record<string,string|undefined>} options.env
 * @param {(path: string) => boolean} options.exists
 * @param {() => string} options.playwrightExecutablePath  may throw when no browser is cached
 * @returns {{binary: string, source: string} | {binary: null, reason: string, attempted: string[]}}
 */
export function resolveBrowserBinary({ platform, env, exists, playwrightExecutablePath }) {
  const attempted = [];

  const override = env.CHROME_BIN;
  if (override) {
    attempted.push(`CHROME_BIN=${override}`);
    // An explicit override that does not exist is a configuration error, not a
    // reason to silently fall through to some other browser.
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
    attempted.push(`playwright-core (${error instanceof Error ? error.message.split('\n')[0] : String(error)})`);
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
