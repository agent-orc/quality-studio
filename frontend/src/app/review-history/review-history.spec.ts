import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ReviewHistoryDetail, ReviewHistorySummary, ReviewRunDiffResponse } from '../quality-api';
import { ReviewHistory } from './review-history';

describe('ReviewHistory', () => {
  let http: HttpTestingController;
  const originalUrl = location.href;

  beforeEach(() => {
    history.replaceState(null, '', '?');
    TestBed.configureTestingModule({
      imports: [ReviewHistory],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    history.replaceState(null, '', originalUrl);
    delete document.documentElement.dataset['theme'];
  });

  it('loads filters and cursor pages only after the History entry opens', async () => {
    const fixture = TestBed.createComponent(ReviewHistory);
    fixture.detectChanges();
    http.expectNone('/api/repos/default/review/history');

    const opening = fixture.componentInstance.toggle();
    http.expectOne(request => request.url === '/api/repos/default/review/history' &&
      request.params.get('limit') === '20').flush({ runs: [summary('one')], nextCursor: 'next' });
    await opening;

    fixture.componentInstance.kind.set('code');
    fixture.componentInstance.path.set('src');
    fixture.componentInstance.outcome.set('done');
    const filtering = fixture.componentInstance.applyFilters();
    http.expectOne(request => request.url === '/api/repos/default/review/history' &&
      request.params.get('kind') === 'code' && request.params.get('path') === 'src' &&
      request.params.get('outcome') === 'done').flush({ runs: [summary('filtered')], nextCursor: 'more' });
    await filtering;

    const paging = fixture.componentInstance.loadMore();
    http.expectOne(request => request.params.get('cursor') === 'more').flush({ runs: [summary('older')], nextCursor: null });
    await paging;
    expect(fixture.componentInstance.rows().map(run => run.runId)).toEqual(['filtered', 'older']);
  });

  it('keeps exactly two comparable selections in URL state and renders warnings before deltas', async () => {
    const fixture = TestBed.createComponent(ReviewHistory);
    const component = fixture.componentInstance;
    const first = summary('before');
    const second = summary('after');
    const incompatible = summary('security', 'security');

    const opening = component.toggle();
    http.expectOne('/api/repos/default/review/history?limit=20').flush({ runs: [first, second, incompatible], nextCursor: null });
    await opening;
    await component.toggleCompare(first);
    const comparing = component.toggleCompare(second);
    http.expectOne(request => request.url === '/api/repos/default/review/history/after/diff' &&
      request.params.get('against') === 'before').flush(diffFixture());
    await comparing;

    expect(component.selected().map(run => run.runId)).toEqual(['before', 'after']);
    expect(component.canSelect(incompatible)).toBeFalse();
    expect(new URLSearchParams(location.search).get('compare')).toBe('before,after');
    fixture.detectChanges();
    const comparison = fixture.nativeElement.querySelector('.history-diff');
    expect(comparison.querySelector('.comparison-warnings')?.textContent).toContain('model-changed');
    expect(comparison.querySelector('.diff-grid')?.textContent).toContain('Scope');

    const selecting = component.selectRun(second);
    http.expectOne('/api/repos/default/review/history/after').flush(detailFixture('after'));
    await selecting;
    expect(new URLSearchParams(location.search).get('history')).toBe('after');
  });

  it('uses keyboard-reachable controls and renders under both central themes', async () => {
    const fixture = TestBed.createComponent(ReviewHistory);
    const opening = fixture.componentInstance.toggle();
    http.expectOne('/api/repos/default/review/history?limit=20').flush({ runs: [summary('one')], nextCursor: null });
    await opening;
    for (const theme of ['light', 'dark']) {
      document.documentElement.dataset['theme'] = theme;
      fixture.detectChanges();
      const buttons = [...fixture.nativeElement.querySelectorAll('button')] as HTMLButtonElement[];
      expect(buttons.length).toBeGreaterThan(1);
      expect(buttons.every(button => button.type === 'button')).toBeTrue();
      expect(fixture.nativeElement.querySelector('[aria-label="History filters"]')).not.toBeNull();
    }
  });

  it('restores detail and two-run comparison from URL state after reopening', async () => {
    history.replaceState(null, '', '?history=after&compare=before,after');
    const fixture = TestBed.createComponent(ReviewHistory);
    fixture.detectChanges();
    await Promise.resolve();

    http.expectOne('/api/repos/default/review/history?limit=20')
      .flush({ runs: [summary('before'), summary('after')], nextCursor: null });
    await Promise.resolve();
    await Promise.resolve();
    http.expectOne('/api/repos/default/review/history/after').flush(detailFixture('after'));
    await Promise.resolve();
    await Promise.resolve();
    http.expectOne(request => request.url === '/api/repos/default/review/history/after/diff' &&
      request.params.get('against') === 'before').flush(diffFixture());
    await fixture.whenStable();

    expect(fixture.componentInstance.open()).toBeTrue();
    expect(fixture.componentInstance.detail()?.run.runId).toBe('after');
    expect(fixture.componentInstance.selected().map(run => run.runId)).toEqual(['before', 'after']);
    expect(fixture.componentInstance.diff()?.beforeRunId).toBe('before');
  });

  it('keeps filters and rows inside a compact responsive host', async () => {
    const fixture = TestBed.createComponent(ReviewHistory);
    fixture.nativeElement.style.display = 'block';
    fixture.nativeElement.style.width = '320px';
    const opening = fixture.componentInstance.toggle();
    http.expectOne('/api/repos/default/review/history?limit=20')
      .flush({ runs: [summary('a-very-long-run-identifier')], nextCursor: null });
    await opening;
    fixture.detectChanges();

    const hostBounds = fixture.nativeElement.getBoundingClientRect();
    const filters = fixture.nativeElement.querySelector('.history-filters') as HTMLElement;
    const row = fixture.nativeElement.querySelector('.history-row') as HTMLElement;
    expect(getComputedStyle(filters).display).toBe('grid');
    expect(row.getBoundingClientRect().right).toBeLessThanOrEqual(hostBounds.right + 0.5);
  });

  function summary(runId: string, kind: 'code' | 'security' = 'code'): ReviewHistorySummary {
    return {
      runId, repositoryId: 'default', createdAt: '2026-08-11T08:00:00Z', path: 'src', level: 'project', kind,
      outcome: 'done', complete: true, attempt: 1, startedAt: '2026-08-11T08:00:00Z',
      finishedAt: '2026-08-11T08:01:00Z', operations: 2, findings: 1,
      quality: { lowestGrade: 80, lowestBand: 'B', worstSecurityVerdict: null, activeFindings: 1, highestActiveSeverity: 'high' },
    };
  }

  function detailFixture(runId: string): ReviewHistoryDetail {
    return {
      run: {
        runId, repositoryId: 'default', createdAt: '2026-08-11T08:00:00Z',
        subject: { id: 'root', name: 'Root', path: 'src' }, level: 'project', kind: 'code', targets: [],
        configuration: {
          model: 'gpt-5', thinkingLevel: 'high', cliType: 'codex', force: false,
          tokenCap: null, costCap: null, estimate: null, recommendation: null, routeOverride: false,
        },
        sourceRevision: { commit: null, dirty: null },
      },
      attempt: {
        runId, attempt: 1, outcome: 'done', complete: true, startedAt: '2026-08-11T08:00:00Z',
        finishedAt: '2026-08-11T08:01:00Z', archivedAt: '2026-08-11T08:01:01Z',
        counters: { totalFiles: 1, completedFiles: 1, failedFiles: 0, skippedFiles: 0, usageOperations: 1 },
        cumulativeCounters: { totalFiles: 1, completedFiles: 1, failedFiles: 0, skippedFiles: 0, usageOperations: 1 },
        spend: { tokens: tokens(), cost: null, currency: null, priceStatus: 'unknownModel' },
        cumulativeSpend: { tokens: tokens(), cost: null, currency: null, priceStatus: 'unknownModel' },
        errorCodes: [], ledgerMonths: [], operationIds: [],
        quality: { lowestGrade: null, lowestBand: null, worstSecurityVerdict: null, activeFindings: 0, highestActiveSeverity: null },
      },
      operations: [], findings: [],
    };
  }

  function diffFixture(): ReviewRunDiffResponse {
    return {
      beforeRunId: 'before', beforeAttempt: 1, afterRunId: 'after', afterAttempt: 1,
      comparability: { labels: ['model-changed'], renameCorrelation: 'unavailable-revisions' },
      scope: { added: [], removed: [], persisting: [], changedHashes: [] }, inputs: [],
      execution: {
        before: { outcome: 'done', complete: true, failedFiles: 0, skippedFiles: 0, durationMs: 10 },
        after: { outcome: 'done', complete: true, failedFiles: 0, skippedFiles: 0, durationMs: 12 },
        failedFilesChange: 0, skippedFilesChange: 0, durationMsChange: 2,
      },
      grades: [], verdicts: [], findings: { new: [], resolved: [], persisting: [] }, findingChanges: [],
      economy: {
        before: { ...tokens(), cost: null, currency: null, priceStatus: 'unknownModel' },
        after: { ...tokens(), cost: null, currency: null, priceStatus: 'unknownModel' },
        inputTokensChange: null, outputTokensChange: null, cachedInputTokensChange: null,
        reasoningOutputTokensChange: null, durationMsChange: 2, costChange: null,
      },
    };
  }

  function tokens() {
    return { inputTokens: null, outputTokens: null, cachedInputTokens: null, reasoningOutputTokens: null, durationMs: 10 };
  }
});
