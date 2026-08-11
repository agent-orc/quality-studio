import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { QualityApi, ReviewFinding, ReviewMetaDocument } from '../quality-api';
import { ReviewPanel } from './review-panel';

describe('ReviewPanel session flow', () => {
  let fixture: ComponentFixture<ReviewPanel>;
  let component: ReviewPanel;
  const openFinding: ReviewFinding = {
    id: 'high-open', fingerprint: `sha256:${'a'.repeat(64)}`, ruleId: 'correctness.high', aspect: 'correctness',
    severity: 'high', title: 'Open high finding', description: 'Open description.', recommendation: 'Fix it.', state: 'open',
    stateTimestamp: '2026-08-11T08:00:00Z', locations: [{ path: 'src/A.cs', range: { start: { line: 8, column: 1 }, end: { line: 10, column: 2 } } }],
  };
  const acceptedFinding: ReviewFinding = {
    ...openFinding, id: 'medium-accepted', fingerprint: `sha256:${'b'.repeat(64)}`, severity: 'medium',
    title: 'Accepted medium finding', state: 'accepted', locations: [{ path: 'src/B.cs', range: { start: { line: 4, column: 1 }, end: { line: 4, column: 5 } } }],
  };
  const waivedFinding: ReviewFinding = {
    ...openFinding, id: 'critical-waived', fingerprint: `sha256:${'c'.repeat(64)}`, severity: 'critical',
    title: 'Waived critical finding', state: 'waived',
  };
  const meta: ReviewMetaDocument = {
    reviewedAt: '2026-08-11T08:00:00Z', kind: 'code', reviewer: { agent: 'reviewer', model: 'model' },
    grade: { score: 80, band: 'B', rationale: 'Test grade.' }, summary: 'Test summary.',
    findings: [waivedFinding, acceptedFinding, openFinding], findingCounts: { open: 1, accepted: 1, waived: 1, falsePositive: 0, resolved: 0 },
  };
  const file = signal({ path: 'src/A.cs', content: '', metaDocuments: [meta], sizeBytes: 0, lineEnding: 'lf' as const, encoding: 'utf-8' as const });
  const api = {
    file,
    reviewRuns: signal([
      { id: 'matching', path: 'src/A.cs', kind: 'code' },
      { id: 'wrong-kind', path: 'src/A.cs', kind: 'security' },
      { id: 'wrong-path', path: 'src/B.cs', kind: 'code' },
    ]),
    reviewError: signal(''),
    usage: signal({ runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, byModel: [] }),
    inputs: signal({}), guidelineTraces: signal([]),
    scan: signal({ freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0 }),
    handoverConfigured: signal(false), handoverDryRun: signal(true), focusedThreadId: signal(null),
    scopeRules: signal({ schema: 'scope.v1', rules: [] }),
    mutateFindingState: jasmine.createSpy('mutateFindingState'),
    loadFile: jasmine.createSpy('loadFile'), loadTree: jasmine.createSpy('loadTree'),
    loadScopeRules: jasmine.createSpy('loadScopeRules'), previewScopeRule: jasmine.createSpy('previewScopeRule'),
    addScopeRule: jasmine.createSpy('addScopeRule'), updateScopeRule: jasmine.createSpy('updateScopeRule'),
    deleteScopeRule: jasmine.createSpy('deleteScopeRule'),
    createTask: jasmine.createSpy('createTask'), pauseReview: jasmine.createSpy('pauseReview'),
    cancelReview: jasmine.createSpy('cancelReview'), resumeReview: jasmine.createSpy('resumeReview'),
    errorMessage: (error: unknown) => error instanceof Error ? error.message : 'request failed',
  };
  const node = { id: 'a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds: { code: { direct: 'fresh', metaPath: 'a.review-meta.json' } }, children: [] };

  beforeEach(async () => {
    file.update(value => ({ ...value, metaDocuments: [meta] }));
    for (const spy of [api.mutateFindingState, api.loadFile, api.loadTree, api.loadScopeRules, api.previewScopeRule,
      api.addScopeRule, api.updateScopeRule, api.deleteScopeRule, api.createTask, api.pauseReview, api.cancelReview,
      api.resumeReview]) spy.calls.reset();
    api.mutateFindingState.and.callFake(async (request: { state: string }) =>
      ({ ...openFinding, state: request.state, stateTimestamp: '2026-08-11T08:01:00Z' }));
    api.loadFile.and.resolveTo(); api.loadTree.and.resolveTo();
    api.loadScopeRules.and.resolveTo(api.scopeRules());
    api.previewScopeRule.and.resolveTo({ index: -1, action: 'exclude', pattern: 'src/A.cs', reason: 'Ignore path', matchedFiles: ['src/A.cs'], widerPattern: false });
    api.addScopeRule.and.resolveTo(api.scopeRules()); api.updateScopeRule.and.resolveTo(api.scopeRules());
    api.deleteScopeRule.and.resolveTo(api.scopeRules());

    await TestBed.configureTestingModule({
      imports: [ReviewPanel],
      providers: [{ provide: QualityApi, useValue: api }],
    }).compileComponents();
    fixture = TestBed.createComponent(ReviewPanel);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('selectedPath', 'src/A.cs');
    fixture.componentRef.setInput('selectedNode', node);
    fixture.componentRef.setInput('selectedFinding', openFinding);
    fixture.detectChanges();
  });

  it('derives visible counts from the filtered queue and filters run history to scope and kind', () => {
    expect(component.visibleFindings().map(finding => finding.id)).toEqual(['high-open', 'medium-accepted']);
    expect(component.scopeRuns().map(run => run.id)).toEqual(['matching']);
    expect(fixture.nativeElement.querySelector('.findings-heading small').textContent).toContain('2 visible');

    component.findingFilter.set('all');
    component.severityFilter.set('critical');
    fixture.detectChanges();
    expect(component.visibleFindings().map(finding => finding.id)).toEqual(['critical-waived']);
    expect(fixture.nativeElement.querySelectorAll('.finding-card').length).toBe(1);
  });

  it('opens run details when archived detail or comparison state is restored from the URL', () => {
    const originalUrl = location.href;
    try {
      history.replaceState(null, '', '?compare=before,after');
      const routedFixture = TestBed.createComponent(ReviewPanel);
      expect(routedFixture.componentInstance.runDrawerOpen()).toBeTrue();
      routedFixture.destroy();
    } finally {
      history.replaceState(null, '', originalUrl);
    }
  });

  it('emits code navigation for current evidence but not for a stale range', () => {
    const selected = jasmine.createSpy('selected');
    component.locationSelect.subscribe(selected);
    component.selectFindingLocation(openFinding);
    expect(selected).toHaveBeenCalledWith({ finding: openFinding, locationIndex: 0 });

    fixture.componentRef.setInput('selectedNode', { ...node, kinds: { code: { direct: 'stale' } } });
    fixture.detectChanges();
    selected.calls.reset();
    component.selectFindingLocation(openFinding);
    expect(selected).not.toHaveBeenCalled();
    expect(component.locationLabel(openFinding)).toContain('source changed');
  });

  it('maps operator disposition to lifecycle state and undoes through optimistic concurrency', async () => {
    component.openDisposition('accept');
    component.stateReason.set('Valid issue.');
    await component.saveDisposition(openFinding);

    expect(api.mutateFindingState).toHaveBeenCalledWith(jasmine.objectContaining({
      state: 'accepted', reason: 'Valid issue.', expectedTimestamp: openFinding.stateTimestamp, expiresAt: null,
    }));
    expect(component.lastMutation()?.appliedState).toBe('accepted');

    await component.undoFindingState({ ...openFinding, state: 'accepted' });
    expect(api.mutateFindingState).toHaveBeenCalledWith(jasmine.objectContaining({
      state: 'open', expectedTimestamp: '2026-08-11T08:01:00Z',
    }));
    expect(component.stateStatus()).toBe('Undone');
  });

  it('does not persist a waiver expiry when Dismiss is changed to False positive', async () => {
    component.openDisposition('dismiss');
    component.stateReason.set('Invalid observation.');
    component.stateExpiry.set('2026-08-20T10:00');
    component.dismissState.set('false-positive');
    await component.saveDisposition(openFinding);

    expect(api.mutateFindingState).toHaveBeenCalledWith(jasmine.objectContaining({
      state: 'false-positive', expiresAt: null,
    }));
  });

  it('reloads the recoverable current finding after an optimistic conflict', async () => {
    const current = { ...openFinding, state: 'waived' as const, stateTimestamp: '2026-08-11T08:02:00Z' };
    api.mutateFindingState.and.rejectWith({ status: 409 });
    api.loadFile.and.callFake(async () => file.update(value => ({
      ...value, metaDocuments: [{ ...meta, findings: [current] }],
    })));
    const selected = jasmine.createSpy('selected');
    component.findingSelect.subscribe(selected);
    component.stateReason.set('Concurrent attempt.');

    await component.setFindingState(openFinding, 'accepted');

    expect(api.loadFile).toHaveBeenCalledWith('src/A.cs');
    expect(selected).toHaveBeenCalledWith(current);
    expect(component.stateStatus()).toContain('changed elsewhere');
  });

  it('defaults Ignore path to an exact repository rule and previews before writing', async () => {
    await component.openScopeManager(openFinding);
    expect(component.scopePattern()).toBe('src/A.cs');
    expect(api.previewScopeRule).toHaveBeenCalledWith(jasmine.objectContaining({ pattern: 'src/A.cs', action: 'exclude' }));

    await component.saveScopeRule();
    expect(api.addScopeRule).toHaveBeenCalledWith(jasmine.objectContaining({
      pattern: 'src/A.cs', action: 'exclude', confirmExpansion: false,
    }));
    expect(component.scopeStatus()).toContain('future reviews');
  });

  it('edits an existing scope rule through the repository API', async () => {
    const rule = { index: 2, action: 'exclude' as const, pattern: 'src/Old.cs', reason: 'Old reason',
      matchedFiles: ['src/Old.cs'], widerPattern: false };
    await component.editScopeRule(rule);
    component.scopePattern.set('src/New.cs');
    component.scopeReason.set('New reason');
    component.scopePreview.set({ ...rule, pattern: 'src/New.cs', reason: 'New reason' });

    await component.saveScopeRule();

    expect(api.updateScopeRule).toHaveBeenCalledWith(2, jasmine.objectContaining({
      pattern: 'src/New.cs', reason: 'New reason',
    }));
    expect(component.editingScopeRuleIndex()).toBeNull();
  });
});
