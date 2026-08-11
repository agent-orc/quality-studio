import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const dossierOutput = process.env.QS_DOSSIER_ASSETS
  ? resolve(process.env.QS_DOSSIER_ASSETS)
  : null;
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || (process.platform === 'win32'
  ? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
  : chromium.executablePath());

await mkdir(output, { recursive: true });
if (dossierOutput) await mkdir(dossierOutput, { recursive: true });
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: process.platform === 'linux' ? ['--no-sandbox'] : [],
});
const page = await browser.newPage({ viewport: { width: 1440, height: 960 }, deviceScaleFactor: 1 });

await page.setContent(`<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <style>
      :root { color-scheme: dark; font-family: Inter, "Segoe UI", sans-serif; }
      * { box-sizing: border-box; }
      body { margin: 0; background: #101114; color: #f5f6f8; }
      header { height: 68px; display: flex; align-items: center; gap: 14px; padding: 0 28px; border-bottom: 1px solid #30343b; background: #181a1f; }
      .mark { display: grid; place-items: center; width: 32px; height: 32px; border-radius: 8px; background: #6f5ce7; font-weight: 800; }
      header div { display: grid; gap: 2px; }
      header strong { font-size: 15px; }
      header span, dt { color: #9ba2ad; font-size: 12px; }
      main { display: grid; grid-template-rows: auto minmax(0, 1fr); height: calc(100vh - 68px); }
      .contract { display: grid; grid-template-columns: 220px minmax(0, 1fr) 110px 110px; gap: 18px; align-items: center; padding: 18px 28px; border-bottom: 1px solid #30343b; background: #14161a; }
      dl, dd { margin: 0; min-width: 0; }
      dt { margin-bottom: 4px; text-transform: uppercase; letter-spacing: .08em; }
      dd { font: 13px/1.4 ui-monospace, SFMono-Regular, Consolas, monospace; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .ok { color: #78d7a2; }
      .frame-wrap { min-height: 0; padding: 18px; }
      iframe { width: 100%; height: 100%; border: 1px solid #3b4049; border-radius: 10px; background: white; }
    </style>
  </head>
  <body>
    <header><span class="mark">A</span><div><strong>Agent Studio URL Preview probe</strong><span>Display-only receiver view · iframe source remains mounted</span></div></header>
    <main>
      <section class="contract" aria-label="Received navigation contract">
        <dl><dt>Contract</dt><dd id="tags">Waiting…</dd></dl>
        <dl><dt>Reported URL</dt><dd id="reported-url">Waiting for embedded navigation…</dd></dl>
        <dl><dt>Origin</dt><dd id="origin">—</dd></dl>
        <dl><dt>Messages</dt><dd id="count">0 accepted</dd></dl>
      </section>
      <div class="frame-wrap"><iframe id="quality-studio" title="Embedded Quality Studio"></iframe></div>
    </main>
    <script>
      const frame = document.querySelector('#quality-studio');
      const accepted = [];
      window.__acceptedNavigation = accepted;
      window.addEventListener('message', event => {
        if (event.source !== frame.contentWindow) return;
        const message = event.data;
        if (!message || message.source !== 'url-preview-embed' || message.type !== 'navigation' || typeof message.url !== 'string') return;
        const parsed = new URL(message.url);
        if (parsed.origin !== event.origin) return;
        accepted.push({ message, origin: event.origin, keys: Object.keys(message).sort() });
        document.querySelector('#tags').textContent = message.source + ' / ' + message.type;
        document.querySelector('#tags').className = 'ok';
        document.querySelector('#reported-url').textContent = message.url;
        document.querySelector('#origin').textContent = event.origin;
        document.querySelector('#count').textContent = accepted.length + ' accepted';
      });
    <\/script>
  </body>
</html>`);

const initialUrl = new URL(baseUrl);
initialUrl.searchParams.set('theme', 'dark');
initialUrl.searchParams.set('repo', 'default');
initialUrl.searchParams.set('path', 'src/QualityStudio.Api/Program.cs');
initialUrl.searchParams.set('kind', 'code');
await page.locator('#quality-studio').evaluate((frame, url) => { frame.src = url; }, initialUrl.toString());

await page.waitForFunction(() => window.__acceptedNavigation?.length > 0);
await page.locator('#quality-studio').contentFrame().locator('.embedded-badge').waitFor();
const initial = await readAndAssertLastMessage(page, {
  repository: 'default',
  path: 'src/QualityStudio.Api/Program.cs',
  kind: 'code',
});
await capture(
  page,
  'url-preview-embed-before-navigation.png',
  'qs-88-url-preview-before-dark--real.png',
);

const frame = page.locator('#quality-studio').contentFrame();
await frame.locator('select[aria-label="Review kind"]').first().selectOption('security');
await page.waitForFunction(previousCount => window.__acceptedNavigation?.length > previousCount, initial.count);
const navigated = await readAndAssertLastMessage(page, {
  repository: 'default',
  path: 'src/QualityStudio.Api/Program.cs',
  kind: 'security',
});
await capture(
  page,
  'url-preview-embed-after-navigation.png',
  'qs-88-url-preview-after-dark--real.png',
);

const evidence = {
  capturedAt: new Date().toISOString(),
  baseUrl,
  receiverRules: {
    iframeSourceMatched: true,
    exactTagsMatched: true,
    stringAndParseableUrl: true,
    eventOriginMatchedUrlOrigin: true,
  },
  initial,
  navigated,
  iframeMountedOnce: true,
  screenshots: [
    'url-preview-embed-before-navigation.png',
    'url-preview-embed-after-navigation.png',
  ],
};
await writeFile(
  join(output, 'url-preview-embed-evidence.json'),
  `${JSON.stringify(evidence, null, 2)}\n`,
);
await browser.close();
console.log(JSON.stringify(evidence, null, 2));

async function capture(browserPage, evidenceName, dossierName) {
  const screenshot = await browserPage.screenshot({ fullPage: true });
  await writeFile(join(output, evidenceName), screenshot);
  if (dossierOutput) await writeFile(join(dossierOutput, dossierName), screenshot);
}

async function readAndAssertLastMessage(browserPage, expected) {
  const state = await browserPage.evaluate(() => {
    const messages = window.__acceptedNavigation;
    const last = messages[messages.length - 1];
    const frame = document.querySelector('#quality-studio');
    return {
      count: messages.length,
      message: last.message,
      keys: last.keys,
      eventOrigin: last.origin,
      iframeSrc: frame.src,
    };
  });
  if (JSON.stringify(state.keys) !== JSON.stringify(['source', 'type', 'url'])) {
    throw new Error(`Unexpected payload keys: ${state.keys.join(', ')}`);
  }
  if (state.message.source !== 'url-preview-embed' || state.message.type !== 'navigation') {
    throw new Error(`Unexpected message tags: ${JSON.stringify(state.message)}`);
  }
  const reported = new URL(state.message.url);
  if (reported.origin !== state.eventOrigin) throw new Error('Reported URL origin does not match event.origin');
  if (reported.searchParams.get('repo') !== expected.repository
      || reported.searchParams.get('path') !== expected.path
      || reported.searchParams.get('kind') !== expected.kind) {
    throw new Error(`Reported URL does not match navigation: ${reported}`);
  }
  return state;
}
