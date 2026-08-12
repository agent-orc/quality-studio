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
      :root {
        color-scheme: dark;
        font-family: Inter, "Segoe UI", sans-serif;
        --probe-space-1: 4px;
        --probe-space-2: 8px;
        --probe-space-3: 12px;
        --probe-space-4: 16px;
        --probe-space-5: 24px;
        --probe-surface-1: #101114;
        --probe-surface-2: #181a1f;
        --probe-surface-3: #14161a;
        --probe-line: #30343b;
        --probe-frame-line: #3b4049;
        --probe-fg: #f5f6f8;
        --probe-muted: #9ba2ad;
        --probe-accent: #6f5ce7;
        --probe-ok: #78d7a2;
      }
      * { box-sizing: border-box; }
      body { margin: 0; background: var(--probe-surface-1); color: var(--probe-fg); }
      header {
        height: 68px;
        display: flex;
        align-items: center;
        gap: var(--probe-space-3);
        padding: 0 var(--probe-space-5);
        border-bottom: 1px solid var(--probe-line);
        background: var(--probe-surface-2);
      }
      .mark {
        display: grid;
        place-items: center;
        width: 32px;
        height: 32px;
        border-radius: var(--probe-space-2);
        background: var(--probe-accent);
        font-weight: 800;
      }
      header div { display: grid; gap: 2px; }
      header strong { font-size: 15px; }
      header span, dt { color: var(--probe-muted); font-size: 12px; }
      main { display: grid; grid-template-rows: auto minmax(0, 1fr); height: calc(100vh - 68px); }
      .contract {
        display: grid;
        grid-template-columns: 220px minmax(0, 1fr) 110px 110px;
        gap: var(--probe-space-4);
        align-items: center;
        padding: var(--probe-space-4) var(--probe-space-5);
        border-bottom: 1px solid var(--probe-line);
        background: var(--probe-surface-3);
      }
      dl, dd { margin: 0; min-width: 0; }
      dt { margin-bottom: var(--probe-space-1); text-transform: uppercase; letter-spacing: .08em; }
      dd {
        font: 13px/1.4 ui-monospace, SFMono-Regular, Consolas, monospace;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .ok { color: var(--probe-ok); }
      .frame-wrap { min-height: 0; padding: var(--probe-space-4); }
      iframe {
        width: 100%;
        height: 100%;
        border: 1px solid var(--probe-frame-line);
        border-radius: var(--probe-space-2);
        background: white;
      }
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
      window.__frameLoadCount = 0;
      frame.addEventListener('load', () => window.__frameLoadCount += 1);
      window.addEventListener('message', event => {
        if (event.source !== frame.contentWindow) return;
        const message = event.data;
        if (!message || message.source !== 'url-preview-embed' || message.type !== 'navigation' || typeof message.url !== 'string') return;
        let parsed;
        try { parsed = new URL(message.url); } catch { return; }
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
const embeddedApp = page.locator('#quality-studio').contentFrame();
await embeddedApp.locator('.embedded-badge').waitFor();
await embeddedApp.locator('[data-connection-state="live"]').waitFor();
await embeddedApp.locator('.code-line').first().waitFor();
const initial = await readAndAssertLastMessage(page, {
  repository: 'default',
  path: 'src/QualityStudio.Api/Program.cs',
  kind: 'code',
});
await capture(
  page,
  'url-preview-embed-before-navigation--real.png',
  'qs-88-url-preview-before-dark--real.png',
);

await embeddedApp.locator('select[aria-label="Review kind"]').first().selectOption('security');
await page.waitForFunction(previousCount => window.__acceptedNavigation?.length > previousCount, initial.count);
const navigated = await readAndAssertLastMessage(page, {
  repository: 'default',
  path: 'src/QualityStudio.Api/Program.cs',
  kind: 'security',
});
if (navigated.iframeSrc !== initial.iframeSrc) throw new Error('Receiver remounted the iframe source');
if (navigated.frameLoadCount !== initial.frameLoadCount) throw new Error('Embedded navigation reloaded the iframe');
await capture(
  page,
  'url-preview-embed-after-navigation--real.png',
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
  iframeMountedOnce: navigated.frameLoadCount === initial.frameLoadCount,
  screenshots: [
    'url-preview-embed-before-navigation--real.png',
    'url-preview-embed-after-navigation--real.png',
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
      frameLoadCount: window.__frameLoadCount,
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
