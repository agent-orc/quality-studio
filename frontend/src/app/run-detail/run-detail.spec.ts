import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityRunReport, QualityRunTrendPage } from '../quality-api';
import { RunDetail } from './run-detail';

describe('RunDetail', () => {
  let fixture: ComponentFixture<RunDetail>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RunDetail],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(RunDetail);
    fixture.componentRef.setInput('report', report);
    fixture.componentRef.setInput('trend', trend);
    fixture.detectChanges();
  });

  it('shows partial truth, unit outcomes, provenance, and all text-only exports', () => {
    const element = fixture.nativeElement as HTMLElement;
    const exports = Array.from(element.querySelectorAll<HTMLAnchorElement>('.run-export-actions a'));

    expect(element.textContent).toContain('Partial report');
    expect(element.textContent).toContain('skipped-fresh');
    expect(element.textContent).toContain('high · codex');
    expect(exports.map(link => link.textContent?.trim())).toEqual(['html', 'markdown', 'sarif', 'json']);
    expect(exports.map(link => link.download)).toEqual([
      'quality-run-review-1.html', 'quality-run-review-1.md', 'quality-run-review-1.sarif', 'quality-run-review-1.json',
    ]);
  });

  it('renders incomplete runs as trend events without presenting a comparable score', () => {
    const element = fixture.nativeElement as HTMLElement;
    const points = element.querySelectorAll('[aria-label="Comparable run trend"] [role="listitem"]');

    expect(points.length).toBe(2);
    expect(points[1].classList).toContain('partial');
    expect(points[1].textContent).toContain('event');
    expect(points[1].textContent).toContain('1 reused');
    expect(points[1].textContent).toContain('1 skipped');
    expect(points[1].textContent).toContain('cost unavailable');
  });
});

const report = {
  schemaVersion: 1,
  run: { id: 'review-1', revision: 1, repositoryId: 'default', repositoryName: 'Default', kind: 'code', scope: { unitId: 'unit:file', level: 'file', path: 'Sample.cs', displayName: 'Sample' }, state: 'capped', completeness: 'partial', createdAt: '2026-08-11T07:00:00Z', startedAt: '2026-08-11T07:00:01Z', finishedAt: '2026-08-11T07:00:02Z', model: 'gpt-5.6', thinkingLevel: 'high', cliType: 'codex', force: false },
  subject: { manifestHash: 'sha256:test', targets: [{ unitId: 'unit:file', path: 'Sample.cs', subjectHash: 'hash' }] },
  execution: { reviewed: 0, reusedFresh: 1, failed: 0, skipped: 1, cancelled: 0, aggregateOutcome: null, errors: [], usageOperations: 0, usage: { inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 1 }, tokenCap: 10, costCap: null, costSpent: null, currency: null, priceStatus: 'known', stopReason: 'Token cap reached.' },
  observations: [{ path: 'Sample.cs', level: 'file', outcome: 'skipped-fresh', producedByRun: false, aggregate: false, sidecarPath: '.quality/reviews/sample.json', sidecarSha256: 'sha256:test', reviewedHash: 'hash', providerRunId: 'provider', reviewedAt: '2026-08-11T07:00:00Z', grade: { score: 80, band: 'B', rationale: 'Good' }, summary: 'Summary', findings: [], deterministicEvidence: [] }],
  delta: { status: 'unavailable', reason: 'The current run is partial.', previousRunId: null, new: [], persisting: [], resolved: [], stateChanged: [] },
  summary: { score: null, grade: null, findings: { total: 0, bySeverity: {}, byState: {} }, highestSeverity: null, partialReason: 'Token cap reached.' },
} satisfies QualityRunReport;

const trend = {
  repositoryId: 'default', kind: 'code', scope: report.run.scope, page: 1, pageSize: 30, total: 2,
  points: [
    { runId: 'review-0', revision: 1, finishedAt: '2026-08-10T07:00:00Z', state: 'done', completeness: 'complete', comparable: true, score: 85, grade: 'B', activeFindings: 1, newFindings: 1, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 1, reusedFresh: 0, failed: 0, skipped: 0, inputTokens: 10, outputTokens: 5, durationMs: 10, cost: null, currency: null, partialReason: null },
    { runId: 'review-1', revision: 1, finishedAt: '2026-08-11T07:00:00Z', state: 'capped', completeness: 'partial', comparable: false, score: null, grade: null, activeFindings: 0, newFindings: 0, persistingFindings: 0, resolvedFindings: 0, stateChangedFindings: 0, reviewed: 0, reusedFresh: 1, failed: 0, skipped: 1, inputTokens: 0, outputTokens: 0, durationMs: 1, cost: null, currency: null, partialReason: 'Token cap reached.' },
  ],
} satisfies QualityRunTrendPage;
