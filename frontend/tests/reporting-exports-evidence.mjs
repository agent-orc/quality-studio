import { chromium } from 'playwright-core';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const output = resolve(process.argv[2] ?? process.env.JOB_RESULTS_DIR ?? 'evidence');
const baseUrl = process.env.QS_URL ?? 'http://127.0.0.1:4200/';
const executablePath = process.env.CHROME_BIN || chromium.executablePath();
const hash = `sha256:${'a'.repeat(64)}`;
const kinds = Object.fromEntries(['code', 'security', 'performance'].map(kind => [kind, {
  direct: kind === 'code' ? 'fresh' : 'missing', descendants: kind === 'code' ? 'fresh' : 'missing',
  overall: kind === 'code' ? 'fresh' : 'missing', score: kind === 'code' ? 84 : null,
  band: kind === 'code' ? 'B' : null, metaPath: kind === 'code' ? '.quality/reviews/a.json' : null,
}]));
const run = {
  id: 'review-portable', repositoryId: 'default', path: 'src/A.cs', level: 'file', kind: 'code',
  model: 'gpt-5.6-sol', thinkingLevel: 'high', cliType: 'codex', state: 'capped', totalFiles: 2,
  completedFiles: 1, failedFiles: 0, createdAt: '2026-08-11T08:00:00Z',
  startedAt: '2026-08-11T08:00:01Z', finishedAt: '2026-08-11T08:01:00Z',
  files: [], errors: [], usageOperations: 1,
  usage: { inputTokens: 1200, outputTokens: 240, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 4200 },
  estimate: null, tokenCap: 1400, costCap: null, costSpent: null, currency: null,
  priceStatus: 'unknownModel', skippedFiles: 1, aggregateState: null,
  stopReason: 'Token cap reached after the first completed unit.', deviation: null,
};
const delta = { status: 'unavailable', priorRunId: null, new: [], persisting: [], resolved: [], stateChanged: [] };
const report = {
  schemaVersion: 1, revision: 1,
  run: { ...run, repositoryName: 'Evidence repository', scope: { unitId: 'file-a', level: 'file', path: 'src/A.cs' }, completeness: 'partial', force: false, partialReason: run.stopReason },
  execution: { outcome: 'capped', reviewed: 1, reusedFresh: 0, failed: 0, skipped: 1, cancelled: 0,
    usageOperations: 1, usage: run.usage, costSpent: null, currency: null, priceStatus: 'unknownModel', tokenCap: 1400, costCap: null, errors: [] },
  observations: [{ unitId: 'file-a', path: 'src/A.cs', level: 'file', outcome: 'reviewed', producedByRun: true,
    sidecarPath: '.quality/reviews/files/a.review-meta.code.json', sidecarSha256: hash, reviewedHash: hash,
    providerRunId: 'provider-1', reviewedAt: '2026-08-11T08:00:50Z', grade: { score: 84, band: 'B', rationale: 'Sound with one issue.' },
    summary: 'One issue remains.', findings: [{ repositoryId: 'default', id: 'finding-1', ruleId: 'quality.boundary', kind: 'code', severity: 'medium', state: 'open', title: 'Boundary result needs explicit ownership', description: 'The result crosses a boundary without an owner.', recommendation: 'Assign the boundary owner.', fingerprint: hash, locations: [{ path: 'src/A.cs', startLine: 1, startColumn: 1, endLine: 1, endColumn: 10 }], source: 'agent', sensorId: null, producer: null }], error: null },
    { unitId: 'file-b', path: 'src/B.cs', level: 'file', outcome: 'skipped', producedByRun: false, sidecarPath: null,
      sidecarSha256: null, reviewedHash: null, providerRunId: null, reviewedAt: null, grade: null, summary: null, findings: [], error: run.stopReason }],
  delta,
  summary: { score: null, grade: null, activeFindings: 1, bySeverity: { medium: 1 }, byState: { open: 1 }, highestSeverity: 'medium', partialReason: run.stopReason },
};
const trend = { repositoryId: 'default', kind: 'code', scope: report.run.scope, page: 1, pageSize: 30, total: 2, points: [
  { runId: run.id, revision: 1, finishedAt: run.finishedAt, state: 'capped', completeness: 'partial', model: run.model, cliType: run.cliType, score: null, grade: null, scoreUnavailableReason: run.stopReason, activeFindings: 1, delta, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 1, inputTokens: 1200, outputTokens: 240, durationMs: 4200, costSpent: null, currency: null, connectScore: false },
  { runId: 'review-prior', revision: 1, finishedAt: '2026-08-10T08:01:00Z', state: 'done', completeness: 'complete', model: run.model, cliType: run.cliType, score: 91, grade: 'A', scoreUnavailableReason: null, activeFindings: 0, delta, reviewed: 2, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 2100, outputTokens: 410, durationMs: 7600, costSpent: null, currency: null, connectScore: true },
] };

function json(route, body, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

await mkdir(output, { recursive: true });
const browser = await chromium.launch({ executablePath, headless: true, args: process.platform === 'linux' ? ['--no-sandbox'] : [] });
const variants = [
  { name: 'light', theme: 'light', viewport: { width: 1600, height: 1000 }, reviewWidth: 500 },
  { name: 'dark', theme: 'dark', viewport: { width: 1600, height: 1000 }, reviewWidth: 500 },
  { name: 'narrow', theme: 'light', viewport: { width: 980, height: 900 }, reviewWidth: 430 },
];
const evidence = [];

for (const variant of variants) {
  const page = await browser.newPage({ viewport: variant.viewport });
  await page.emulateMedia({ colorScheme: variant.theme, reducedMotion: 'reduce' });
  await page.addInitScript(({ reviewWidth, narrow }) => localStorage.setItem('qs-layout', JSON.stringify({
    explorerVisible: !narrow, reviewVisible: true, explorerWidth: 260, reviewWidth,
  })), { reviewWidth: variant.reviewWidth, narrow: variant.name === 'narrow' });
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path === '/api/repos') return json(route, { repositories: [{ id: 'default', displayName: 'Evidence repository', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: null, defaultReviewCostCap: null }], defaultRepositoryId: 'default' });
    if (path === '/api/models') return json(route, { schemaVersion: 1, policyVersion: 'evidence', evidenceAsOfDate: '2026-08-11', sourceRepository: 'fixture', sourceCommit: 'fixture', thinkingLevels: ['high'], models: [] });
    if (path.endsWith('/tree')) return json(route, { nodes: [{ id: 'project', name: 'Evidence repository', path: '.', level: 'project', kinds, children: [{ id: 'file-a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds, children: [] }] }] });
    if (path.endsWith('/file')) return json(route, { path: 'src/A.cs', content: 'public sealed class A { }\n', sizeBytes: 26, lineEnding: 'lf', encoding: 'utf-8', metaDocuments: [{ reviewedAt: '2026-08-11T08:00:50Z', kind: 'code', reviewer: { agent: 'reviewer', model: run.model }, grade: { score: 84, band: 'B', rationale: 'Sound with one issue.' }, summary: 'One issue remains.', findings: [], threads: [] }] });
    if (path.endsWith('/scan')) return json(route, { files: [], freshCount: 1, staleCount: 0, policyDriftCount: 0, missingCount: 0 });
    if (path.endsWith('/inputs')) return json(route, { kinds: Object.fromEntries(['code', 'security', 'performance'].map(kind => [kind, { kind, level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] }])) });
    if (path.endsWith('/guidelines')) return json(route, { guidelines: [], catalogue: [], traces: [] });
    if (path.endsWith('/risk')) return json(route, { days: 90, currentCommit: null, rows: [], matrix: [] });
    if (path.endsWith('/handover')) return json(route, { targetConfigured: false, dryRun: true });
    if (path.endsWith('/review/runs')) return json(route, { runs: [run] });
    if (path.endsWith(`/review/runs/${run.id}/report`)) return json(route, report);
    if (path.endsWith(`/review/runs/${run.id}/trend`)) return json(route, trend);
    if (path.endsWith('/usage')) return json(route, { generatedAt: '2026-08-11T08:01:00Z', runs: 1, inputTokens: 1200, outputTokens: 240, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 4200, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] });
    if (path === '/api/quotas') return json(route, { at: '2026-08-11T08:01:00Z', ttlSeconds: 600, providers: [] });
    if (path.endsWith('/project')) return json(route, {}, 404);
    return json(route, { title: 'Fixture route not configured' }, 404);
  });
  const url = new URL(baseUrl);
  url.searchParams.set('theme', variant.theme);
  url.searchParams.set('path', 'src/A.cs');
  url.searchParams.set('kind', 'code');
  await page.goto(url.toString());
  await page.getByRole('button', { name: /Run details/ }).click();
  const open = page.getByRole('button', { name: 'Open details' });
  await open.focus();
  await page.keyboard.press('Enter');
  const detail = page.locator('.run-detail-surface');
  await detail.waitFor();
  await page.getByRole('button', { name: 'Export HTML' }).focus();
  const audit = await detail.evaluate(element => ({
    partialLabel: element.querySelector('.run-partial-label')?.textContent?.trim(),
    exportActions: [...element.querySelectorAll('.run-export-actions button')].map(button => button.textContent?.trim()),
    unitOutcomes: element.querySelectorAll('.run-unit-list>div').length,
    partialTrendEvents: element.querySelectorAll('.run-trend>div.partial').length,
    background: getComputedStyle(element).backgroundColor,
  }));
  if (audit.partialLabel !== 'Partial' || audit.exportActions.length !== 4 || audit.unitOutcomes !== 2 || audit.partialTrendEvents !== 1)
    throw new Error(`${variant.name}: run detail acceptance audit failed: ${JSON.stringify(audit)}`);
  const screenshot = `qs-73-reporting-exports-${variant.name}.png`;
  await detail.screenshot({ path: join(output, screenshot) });
  evidence.push({ variant: variant.name, screenshot, viewport: variant.viewport, reducedMotion: true, audit });
  await page.close();
}

await browser.close();
await writeFile(join(output, 'qs-73-reporting-exports-evidence.json'),
  `${JSON.stringify({ capturedAt: new Date().toISOString(), baseUrl, evidence }, null, 2)}\n`);
console.log(JSON.stringify({ output, evidence }, null, 2));
