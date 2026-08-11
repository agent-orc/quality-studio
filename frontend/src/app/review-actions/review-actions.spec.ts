import { ComponentFixture, TestBed } from '@angular/core/testing';
import { computed, signal } from '@angular/core';
import { QualityApi, ReviewModelCatalog, TreeNode } from '../quality-api';
import { ReviewActions } from './review-actions';

describe('ReviewActions', () => {
  let fixture: ComponentFixture<ReviewActions>;
  let component: ReviewActions;
  const catalog: ReviewModelCatalog = {
    schemaVersion: 1,
    policyVersion: '2026-07-24',
    evidenceAsOfDate: '2026-07-24',
    sourceRepository: 'agent-orc/token-economy',
    sourceCommit: 'abc',
    thinkingLevels: ['low', 'medium', 'high', 'xhigh'],
    models: [
      { modelId: 'gpt-5.6-sol', aliases: ['sol'], cliType: 'codex', capabilityTier: 'frontier', suitability: 'Demanding reviews.', routingStatus: 'selectable', supportedThinkingLevels: ['low', 'medium', 'high', 'xhigh'], provisional: false, evidenceStatus: 'observational', note: 'Evidence note.', priceAvailable: false, availableForNewRuns: true },
      { modelId: 'gpt-5.5', aliases: [], cliType: 'codex', capabilityTier: 'balanced', suitability: 'Unsupported.', routingStatus: 'unsupported', supportedThinkingLevels: ['medium'], provisional: false, evidenceStatus: 'unknown', note: 'Not qualified.', priceAvailable: false, availableForNewRuns: false },
      { modelId: 'claude-sonnet-5', aliases: [], cliType: 'claude', capabilityTier: 'frontier', suitability: 'Fallback.', routingStatus: 'fallbackOnly', supportedThinkingLevels: ['high'], provisional: true, evidenceStatus: 'provisional', note: 'Fallback only.', priceAvailable: true, availableForNewRuns: true },
    ],
  };
  const api = {
    modelCatalog: signal(catalog),
    reviewRuns: signal<any[]>([]),
    connected: computed(() => true),
    reviewError: signal(''),
    selectedRepository: signal({ displayName: 'Sample repository' }),
    estimateReview: jasmine.createSpy('estimateReview'),
    startReview: jasmine.createSpy('startReview'),
    pauseReview: jasmine.createSpy('pauseReview'), cancelReview: jasmine.createSpy('cancelReview'), resumeReview: jasmine.createSpy('resumeReview'),
  };
  const node: TreeNode = {
    id: 'sample', name: 'Sample.cs', level: 'file', path: 'Sample.cs', kinds: {}, children: [],
  };

  beforeEach(async () => {
    api.estimateReview.calls.reset();
    api.startReview.calls.reset();
    api.reviewRuns.set([]);
    await TestBed.configureTestingModule({
      imports: [ReviewActions],
      providers: [{ provide: QualityApi, useValue: api }],
    }).compileComponents();
    fixture = TestBed.createComponent(ReviewActions);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.detectChanges();
  });

  it('opens with Runner default first and only routable models for the selected CLI', () => {
    const input = fixture.nativeElement.querySelector('[aria-label="Review model"]') as HTMLInputElement;
    input.dispatchEvent(new FocusEvent('focus'));
    fixture.detectChanges();

    const options = [...fixture.nativeElement.querySelectorAll('[role="option"]')] as HTMLElement[];
    expect(options[0].textContent).toContain('Runner default model');
    expect(options[1].textContent).toContain('gpt-5.6-sol');
    expect(options[1].textContent).toContain('frontier');
    expect(options[1].textContent).toContain('Demanding reviews');
    expect(options.some(option => option.textContent?.includes('gpt-5.5'))).toBeFalse();
    expect(options.some(option => option.textContent?.includes('claude-sonnet-5'))).toBeFalse();
  });

  it('keeps free text as an escape hatch and resets route overrides when the CLI changes', () => {
    component.onModelInput('gpt-6-review-preview');
    component.thinkingLevel.set('high');

    expect(component.model()).toBe('gpt-6-review-preview');
    expect(component.selectedModel()).toBeNull();
    expect(component.thinkingOptions()).toEqual(catalog.thinkingLevels);

    component.selectCli('claude');
    expect(component.model()).toBe('');
    expect(component.thinkingLevel()).toBe('');
    expect(component.modelsForCli().map(model => model.modelId)).toEqual(['claude-sonnet-5']);
  });

  it('accepts scaled token caps and sends the parsed token count', async () => {
    api.estimateReview.and.resolveTo({
      repositoryId: 'default', path: 'Sample.cs', level: 'file', kind: 'code', model: null,
      thinkingLevel: null, cliType: 'codex', tokenCap: 100000, costCap: null, overrideBelowFloor: false,
      estimate: { files: 1, operations: 1, promptCharacters: 4000, inputTokens: 1000, outputTokens: 200,
        cost: null, currency: null, priceStatus: 'unknownModel', historySamples: 0,
        method: 'Rendered prompt characters / 4.', expectedFreshSkips: 0 },
      recommendation: { policyVersion: '2026-07-24', recommendedModel: 'gpt-5.6-sol',
        recommendedThinkingLevel: 'xhigh', capabilityTier: 'frontier', score: 70,
        correctnessFloor: 'sol-xhigh', reason: 'Correctness floor.', selectionSource: 'model-routing-policy' },
    });
    component.setCapKind('tokens');
    component.setTokenCapValue('0.1M');

    await component.prepare();

    expect(component.capValue()).toBe(100_000);
    expect(api.estimateReview).toHaveBeenCalledWith(jasmine.objectContaining({ tokenCap: 100_000, costCap: null }));
  });

  it('renders inline server-owned preflight and explicitly confirms a below-floor start', async () => {
    api.estimateReview.and.resolveTo({
      repositoryId: 'default', path: 'Sample.cs', level: 'file', kind: 'code', model: 'gpt-5.6-sol',
      thinkingLevel: 'low', cliType: 'codex', tokenCap: 100000, costCap: null, overrideBelowFloor: true,
      estimate: { files: 1, operations: 1, promptCharacters: 4000, inputTokens: 1000, outputTokens: 200,
        cost: null, currency: null, priceStatus: 'unknownModel', historySamples: 0,
        method: 'Rendered prompt characters / 4.', expectedFreshSkips: 1 },
      recommendation: { policyVersion: '2026-07-24', recommendedModel: 'gpt-5.6-sol',
        recommendedThinkingLevel: 'xhigh', capabilityTier: 'frontier', score: 70,
        correctnessFloor: 'sol-xhigh', reason: 'Security floor; quota does not lower it.',
        selectionSource: 'model-routing-policy' },
    });
    api.startReview.and.resolveTo({});
    component.model.set('gpt-5.6-sol');
    component.thinkingLevel.set('low');

    await component.prepare();
    fixture.detectChanges();

    const sheet = fixture.nativeElement.querySelector('[aria-label="Review preflight"]') as HTMLElement;
    expect(sheet.textContent).toContain('Sample repository');
    expect(sheet.textContent).toContain('1 expected fresh skips');
    expect(sheet.textContent).toContain('floor sol-xhigh');
    expect(sheet.textContent).toContain('Below the correctness floor');

    await component.start();
    expect(api.startReview).toHaveBeenCalledWith(jasmine.objectContaining({ confirmBelowFloor: true }));
  });

  it('moves to the recommended model provider before re-estimating', async () => {
    api.estimateReview.and.resolveTo({
      repositoryId: 'default', path: 'Sample.cs', level: 'file', kind: 'code', model: 'gpt-5.6-sol',
      thinkingLevel: 'xhigh', cliType: 'codex', tokenCap: null, costCap: null, overrideBelowFloor: false,
      estimate: { files: 1, operations: 1, promptCharacters: 4000, inputTokens: 1000, outputTokens: 200,
        cost: null, currency: null, priceStatus: 'unknownModel', historySamples: 0,
        method: 'Rendered prompt characters / 4.', expectedFreshSkips: 0 },
      recommendation: { policyVersion: '2026-07-24', recommendedModel: 'gpt-5.6-sol',
        recommendedThinkingLevel: 'xhigh', capabilityTier: 'frontier', score: 70,
        correctnessFloor: 'sol-xhigh', reason: 'Correctness floor.', selectionSource: 'model-routing-policy' },
    });
    component.cliType.set('claude');
    component.preflight.set(await api.estimateReview({}));
    api.estimateReview.calls.reset();

    component.useRecommendation();
    await fixture.whenStable();

    expect(component.cliType()).toBe('codex');
    expect(api.estimateReview).toHaveBeenCalledWith(jasmine.objectContaining({
      cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'xhigh',
    }));
  });

  it('presents every run terminal and control state without duplicating the launcher', () => {
    const expected = new Map([
      ['queued', 'Pause'], ['running', 'Pause'], ['paused', 'Resume'], ['capped', 'Raise cap and resume'],
      ['failed', 'Review again'], ['cancelled', 'Review again'], ['done', 'Review again'],
    ]);
    for (const [state, action] of expected) {
      api.reviewRuns.set([{
        id: `run-${state}`, repositoryId: 'default', path: 'Sample.cs', level: 'file', kind: 'code',
        model: null, thinkingLevel: null, cliType: 'codex', state, totalFiles: 2, completedFiles: state === 'done' ? 2 : 1,
        failedFiles: state === 'failed' ? 1 : 0, skippedFiles: state === 'capped' ? 1 : 0, aggregateState: null,
        files: [{ path: 'Sample.cs', state: state === 'done' ? 'done' : 'running', error: null }], stopReason: null,
      }]);
      component.showLauncher.set(false);
      fixture.detectChanges();
      const strip = fixture.nativeElement.querySelector('.active-run-strip') as HTMLElement;
      expect(strip.textContent).toContain(`review · ${state}`);
      expect(strip.textContent).toContain(action);
      expect(fixture.nativeElement.querySelectorAll('.review-intent').length).toBe(0);
    }
  });

  it('focuses the applicable center action when Explorer requests the launcher', async () => {
    api.reviewRuns.set([{
      id: 'run-done', repositoryId: 'default', path: 'Sample.cs', level: 'file', kind: 'code',
      model: null, thinkingLevel: null, cliType: 'codex', state: 'done', totalFiles: 1, completedFiles: 1,
      failedFiles: 0, skippedFiles: 0, aggregateState: null, files: [], stopReason: null,
    }]);
    fixture.detectChanges();
    fixture.componentRef.setInput('focusRequest', 1);
    fixture.detectChanges();
    await fixture.whenStable();

    expect((document.activeElement as HTMLElement).textContent).toContain('Review again');
  });
});
