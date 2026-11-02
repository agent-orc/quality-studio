import { chromium } from 'playwright-core';
import { findBrowserBinary } from './browser-binary.mjs';

// Prints the browser that run-tests.mjs would use, so .agent-studio/test-frontend can
// decide whether to install Playwright Chromium before the Karma run.
const chromeBin = findBrowserBinary({ playwrightExecutablePath: () => chromium.executablePath() });
if (!chromeBin) process.exit(1);
console.log(chromeBin);
