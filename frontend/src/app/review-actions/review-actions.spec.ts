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
    reviewRuns: signal([]),
    connected: computed(() => true),
    reviewError: signal(''),
    estimateReview: jasmine.createSpy('estimateReview'),
    startReview: jasmine.createSpy('startReview'),
  };
  const node: TreeNode = {
    id: 'sample', name: 'Sample.cs', level: 'file', path: 'Sample.cs', kinds: {}, children: [],
  };

  beforeEach(async () => {
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
});
