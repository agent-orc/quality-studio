import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { chromium } from 'playwright-core';

const stage = process.argv[2] ?? 'after';
const output = resolve(process.argv[3] ?? process.env.JOB_RESULTS_DIR ?? 'results');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();

await mkdir(output, { recursive: true });
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: process.platform === 'linux' ? ['--no-sandbox'] : [],
});

const audits = {};
for (const theme of ['light', 'dark']) {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await page.addInitScript(() => {
    localStorage.setItem('qs-layout', JSON.stringify({
      explorerVisible: true,
      reviewVisible: true,
      explorerWidth: 280,
      reviewWidth: 460,
    }));
  });
  await page.route('**/api/**', route => route.abort());
  const path = encodeURIComponent('src/QualityStudio.Api/Program.cs');
  await page.goto(`${baseUrl}/?theme=${theme}&path=${path}&kind=code`);

  const findingCard = page.locator('.finding-card').first();
  const findingMarker = page.locator('.finding-marker').first();
  await findingCard.waitFor();
  await findingMarker.waitFor();
  await page.locator('.review-content').evaluate((content, card) => {
    content.scrollTop = Math.max(0, card.offsetTop - 48);
  }, await findingCard.elementHandle());
  await page.locator('.review-pane').screenshot({
    path: join(output, `${stage}-${theme}-findings-list.png`),
  });

  await findingMarker.click();
  const findingDetail = page.locator('.finding-detail');
  await findingDetail.waitFor();
  await findingDetail.scrollIntoViewIfNeeded();
  await page.screenshot({
    path: join(output, `${stage}-${theme}-finding-code-context.png`),
    fullPage: true,
  });

  audits[theme] = await page.evaluate(() => {
    const parseRgb = value => {
      if (value.startsWith('#')) {
        const hex = value.slice(1);
        const normalized = hex.length === 3
          ? [...hex].map(character => character.repeat(2)).join('')
          : hex;
        return [0, 2, 4].map(index => Number.parseInt(normalized.slice(index, index + 2), 16));
      }
      const values = value.match(/[\d.]+/g)?.map(Number) ?? [];
      return values.slice(0, 3);
    };
    const luminance = value => {
      const channels = parseRgb(value).map(channel => {
        const normalized = channel / 255;
        return normalized <= 0.04045
          ? normalized / 12.92
          : ((normalized + 0.055) / 1.055) ** 2.4;
      });
      return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    };
    const contrast = (foreground, background) => {
      const light = Math.max(luminance(foreground), luminance(background));
      const dark = Math.min(luminance(foreground), luminance(background));
      return Number(((light + 0.05) / (dark + 0.05)).toFixed(2));
    };
    const opaqueBackground = element => {
      let candidate = element;
      while (candidate) {
        const background = getComputedStyle(candidate).backgroundColor;
        if (background && background !== 'rgba(0, 0, 0, 0)') return background;
        candidate = candidate.parentElement;
      }
      return getComputedStyle(document.documentElement).backgroundColor;
    };
    const textStyle = selector => {
      const element = document.querySelector(selector);
      const style = getComputedStyle(element);
      const backgroundColor = opaqueBackground(element);
      return {
        fontSize: style.fontSize,
        lineHeight: style.lineHeight,
        fontWeight: style.fontWeight,
        color: style.color,
        backgroundColor,
        contrastRatio: contrast(style.color, backgroundColor),
      };
    };
    const marker = document.querySelector('.finding-marker').getBoundingClientRect();
    const rootStyle = getComputedStyle(document.documentElement);
    const severityBackground = getComputedStyle(document.querySelector('.severity')).backgroundColor;
    const severityPalette = Object.fromEntries(
      ['critical', 'high', 'medium', 'low', 'info'].map(severity => {
        const color = rootStyle.getPropertyValue(`--studio-severity-${severity}`).trim();
        return [severity, {
          color,
          backgroundColor: severityBackground,
          contrastRatio: color ? contrast(color, severityBackground) : null,
        }];
      }),
    );
    return {
      cardTitle: textStyle('.finding-card b'),
      cardSeverity: textStyle('.finding-card .severity'),
      cardDescription: textStyle('.finding-card p'),
      detailTitle: textStyle('.finding-detail h3'),
      detailDescription: textStyle('.finding-detail > p'),
      detailEvidence: document.querySelector('.finding-evidence')
        ? textStyle('.finding-evidence')
        : null,
      severityPalette,
      markerHitArea: { width: marker.width, height: marker.height },
    };
  });
  await page.close();
}

await writeFile(
  join(output, `${stage}-findings-readability-audit.json`),
  `${JSON.stringify({ stage, capturedAt: new Date().toISOString(), audits }, null, 2)}\n`,
);
await browser.close();

if (stage === 'after') {
  const failures = [];
  for (const [theme, audit] of Object.entries(audits)) {
    for (const key of ['cardDescription', 'detailDescription', 'detailEvidence']) {
      if (audit[key] && Number.parseFloat(audit[key].fontSize) < 13) {
        failures.push(`${theme} ${key} is below 13px`);
      }
      if (audit[key] && audit[key].contrastRatio < 4.5) {
        failures.push(`${theme} ${key} is below 4.5:1 contrast`);
      }
    }
    for (const [severity, values] of Object.entries(audit.severityPalette)) {
      if (values.contrastRatio < 4.5) failures.push(`${theme} ${severity} is below 4.5:1 contrast`);
    }
    if (audit.markerHitArea.width < 24 || audit.markerHitArea.height < 22) {
      failures.push(`${theme} marker hit area is below 24x22px`);
    }
  }
  if (failures.length) throw new Error(`Finding readability audit failed:\n${failures.join('\n')}`);
}
