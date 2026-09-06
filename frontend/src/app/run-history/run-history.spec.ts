import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { ReviewRun } from '../contracts';
import { QualityApi } from '../quality-api';
import { RunHistory } from './run-history';

describe('RunHistory drawer', () => {
  let fixture: ComponentFixture<RunHistory>;
  let component: RunHistory;
  const initialRuns = [
    { id: 'matching', path: 'src/A.cs', kind: 'code' },
    { id: 'wrong-kind', path: 'src/A.cs', kind: 'security' },
    { id: 'wrong-path', path: 'src/B.cs', kind: 'code' },
  ];
  const api = {
    reviewRuns: signal(initialRuns),
    reviewError: signal(''),
    usage: signal({ runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, byModel: [] }),
    loadRunReport: jasmine.createSpy('loadRunReport'),
    loadRunTrend: jasmine.createSpy('loadRunTrend'),
    compareRuns: jasmine.createSpy('compareRuns'),
    loadPinnedRunIds: jasmine.createSpy('loadPinnedRunIds'),
    pinRun: jasmine.createSpy('pinRun'),
    unpinRun: jasmine.createSpy('unpinRun'),
    resumeReview: jasmine.createSpy('resumeReview'),
    runReportUrl: (id: string, format: string) => `/api/repos/default/review/runs/${id}/report?format=${format}`,
    runReportFileName: (id: string, format: string) => `quality-run-${id}.${format}`,
    repositoryReportUrl: () => '/api/repos/default/report?format=html',
    errorMessage: (error: unknown) => error instanceof Error ? error.message : 'request failed',
  };
  const node = { id: 'a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds: { code: { direct: 'fresh' } }, children: [] };

  beforeEach(async () => {
    api.reviewRuns.set(initialRuns);
    api.loadRunReport.calls.reset();
    api.loadRunTrend.calls.reset();
    api.compareRuns.calls.reset();
    api.loadPinnedRunIds.calls.reset();
    api.pinRun.calls.reset();
    api.unpinRun.calls.reset();
    api.loadPinnedRunIds.and.resolveTo([]);
    api.pinRun.and.resolveTo(['pinned-run']);
    api.unpinRun.and.resolveTo([]);

    await TestBed.configureTestingModule({
      imports: [RunHistory],
      providers: [{ provide: QualityApi, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(RunHistory);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.detectChanges();
  });

  it('filters run history to the selected scope and kind', () => {
    expect(component.scopeRuns().map(run => run.id)).toEqual(['matching']);
  });

  it('opens a terminal canonical snapshot with exports and its separate run trend', async () => {
    const run = {
      id: 'terminal', repositoryId: 'default', path: 'src/A.cs', level: 'file', kind: 'code', state: 'done',
      model: 'gpt-test', thinkingLevel: 'high', cliType: 'codex', completedFiles: 1, totalFiles: 1,
      failedFiles: 0, skippedFiles: 0, errors: [], usageOperations: 0, usage: { inputTokens: 0, outputTokens: 0,
        cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 0 }, costSpent: null, currency: null,
      stopReason: null, deviation: null, createdAt: '2026-08-11T08:00:00Z',
    } as any;
    api.reviewRuns.set([run]);
    api.loadRunReport.and.resolveTo({
      run: { id: 'terminal', revision: 1, completeness: 'complete', state: 'done', cliType: 'codex', model: 'gpt-test', thinkingLevel: 'high' },
      subject: { manifestHash: 'sha256:manifest' },
      execution: { reviewed: 1, reusedFresh: 0 },
      summary: { score: 91, grade: 'A', partialReason: null, findings: { total: 1 } },
      observations: [{ unitId: 'a', path: 'src/A.cs', level: 'file', outcome: 'done', producedByRun: true,
        grade: { score: 91, band: 'A', rationale: 'Good.' }, findings: [{ fingerprint: 'sha256:f', severity: 'high', state: 'open', ruleId: 'rule', title: 'Captured', description: 'Evidence.' }] }],
    } as any);
    api.loadRunTrend.and.resolveTo({ points: [{ runId: 'terminal', revision: 1, finishedAt: '2026-08-11T08:00:00Z', state: 'done', completeness: 'complete', comparable: true, comparisonReason: null, score: 91, grade: 'A', activeFindings: 1, newFindings: 1, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 100, outputTokens: 20, cost: null, currency: null }], nextCursor: null });

    component.runDrawerOpen.set(true);
    await component.openRun(run);
    fixture.detectChanges();

    expect(api.loadRunReport).toHaveBeenCalledWith('terminal');
    expect(api.loadRunTrend).toHaveBeenCalledWith('code', 'a', 'file');
    expect(fixture.nativeElement.querySelector('.run-detail-surface').textContent).toContain('complete snapshot');
    expect(fixture.nativeElement.querySelectorAll('.run-exports a').length).toBe(4);
    expect(fixture.nativeElement.querySelector('.commit-trend-note').textContent).toContain('Commit trend');
    expect(fixture.nativeElement.querySelector('.run-findings').textContent).toContain('Captured');
  });

  it('loads pinned baselines when the run drawer opens and can toggle a pin', async () => {
    await component.toggleRunDrawer();
    expect(api.loadPinnedRunIds).toHaveBeenCalled();
    expect(component.isPinned('pinned-run')).toBe(false);

    await component.togglePin('pinned-run');
    expect(api.pinRun).toHaveBeenCalledWith('pinned-run');
    expect(component.isPinned('pinned-run')).toBe(true);

    await component.togglePin('pinned-run');
    expect(api.unpinRun).toHaveBeenCalledWith('pinned-run');
    expect(component.isPinned('pinned-run')).toBe(false);
  });

  it('compares two run outcomes and renders new, resolved, and route-incompatibility warnings', async () => {
    const runFields = {
      path: 'src/A.cs', kind: 'code', state: 'done', completedFiles: 1, totalFiles: 1, failedFiles: 0, skippedFiles: 0,
      errors: [], usageOperations: 1, usage: { inputTokens: 10, outputTokens: 5, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 100 },
      costSpent: null, currency: null, priceStatus: 'unavailable', stopReason: null, deviation: null, createdAt: '2026-08-11T08:00:00Z',
    };
    const baseline = { ...runFields, id: 'baseline' } as any;
    const candidate = { ...runFields, id: 'candidate' } as any;
    api.reviewRuns.set([baseline, candidate]);
    api.compareRuns.and.resolveTo({
      status: 'available',
      baseline: { runId: 'baseline', status: 'found', error: null },
      candidate: { runId: 'candidate', status: 'found', error: null },
      comparison: {
        baselineRunId: 'baseline', candidateRunId: 'candidate',
        route: { compatible: false, differences: ["Model changed from 'a' to 'b'."] },
        new: [{ fingerprint: 'sha256:1', severity: 'high', title: 'New finding', ruleId: 'rule.1', baselineState: null, candidateState: 'open', locations: [] }],
        unchanged: [],
        resolved: [{ fingerprint: 'sha256:2', severity: 'low', title: 'Fixed finding', ruleId: 'rule.2', baselineState: 'open', candidateState: null, locations: [] }],
        dispositionChanged: [],
      },
    });

    component.runDrawerOpen.set(true);
    component.openCompare(candidate);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.compareBaselineId()).toBe('baseline');
    expect(api.compareRuns).toHaveBeenCalledWith('baseline', 'candidate');
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Route or inputs differ');
    expect(text).toContain('New finding');
    expect(text).toContain('Fixed finding');

    // The pickers must show the pair that was actually compared. Note this only guards against a
    // binding being dropped outright: TestBed renders the @for options before applying the binding,
    // so it cannot reproduce the ordering that made a plain [value] binding fall back to the first
    // option in a real browser. tests/run-compare-evidence.mjs asserts that against a live page.
    const baselineSelect: HTMLSelectElement = fixture.nativeElement.querySelector('select[aria-label="Baseline run"]');
    const candidateSelect: HTMLSelectElement = fixture.nativeElement.querySelector('select[aria-label="Candidate run"]');
    expect(baselineSelect.value).toBe('baseline');
    expect(candidateSelect.value).toBe('candidate');
  });

  it('names the model, how it was chosen, and the running cost of each run', () => {
    api.reviewRuns.set([{
      id: 'matching', path: 'src/A.cs', kind: 'code', state: 'running', cliType: 'codex',
      model: 'gpt-5.6-sol', modelSource: 'policy-default', thinkingLevel: 'high',
      totalFiles: 4, completedFiles: 2, failedFiles: 0, skippedFiles: 0, usageOperations: 2,
      usage: { inputTokens: 30_000, outputTokens: 5_000, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 4_000 },
      tokenCap: 100_000, costCap: null, costSpent: 0.42, currency: 'EUR', priceStatus: 'priced', errors: [],
    }] as unknown as ReviewRun[]);
    component.runDrawerOpen.set(true);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('gpt-5.6-sol');
    expect(text).toContain('policy default');
    expect(text).toContain('Tokens 35k tok / 100k tok');
    expect(text).toContain('Cost 0.42 EUR');
  });

  it('states an unpriced run as unpriced with the reason', () => {
    api.reviewRuns.set([{
      id: 'matching', path: 'src/A.cs', kind: 'code', state: 'done', cliType: 'claude',
      model: null, modelSource: 'runner-default', thinkingLevel: null,
      totalFiles: 1, completedFiles: 1, failedFiles: 0, skippedFiles: 0, usageOperations: 1,
      usage: { inputTokens: 10, outputTokens: 2, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 10 },
      tokenCap: null, costCap: null, costSpent: null, currency: null, priceStatus: 'unknownModel', errors: [],
    }] as unknown as ReviewRun[]);
    component.runDrawerOpen.set(true);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('runner default model');
    expect(text).toContain('runner default');
    expect(text).toContain('Cost unpriced (unknown model)');
    expect(text).not.toContain('Cost 0.00');
  });
});
