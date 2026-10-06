import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReviewCriteria } from './review-criteria';

describe('ReviewCriteria', () => {
  let fixture: ComponentFixture<ReviewCriteria>;
  let http: HttpTestingController;
  beforeEach(async () => {
    localStorage.removeItem('qs-last-repository');
    await TestBed.configureTestingModule({ imports: [ReviewCriteria], providers: [provideHttpClient(), provideHttpClientTesting()] }).compileComponents();
    fixture = TestBed.createComponent(ReviewCriteria);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('/api/repos/default/rules').flush({ catalogueVersion: '1.4.0', sources: ['project'], rules: [{
      id: 'QS-GN-003', title: 'Preserve declared architecture', technology: 'generic', kinds: ['code'],
      statement: 'Respect the local contract.', rationale: 'Ownership is discoverable.', detection: 'Compare source paths.',
      enabled: false, severity: 'low', authoredSeverity: 'medium', deterministicRuleIds: ['architecture/missing-directory'],
      goodExample: '<safe text>', badExample: '<script>bad</script>',
    }], traces: [{ id: 'QS-GN-003', source: 'project', enabled: false, severityOverridden: true, reason: 'Migration in progress.' }] });
    http.expectOne('/api/repos/default/inputs').flush({ level: 'file', kinds: { code: {
      kind: 'code', level: 'file', budgetCharacters: 5, includedCharacters: 5, complete: false,
      inputs: [{ id: 'local', source: 'local.md', scope: 'project', priority: 50, content: 'first second', includedContent: 'first', truncated: true }],
      omissions: [{ id: 'local', source: 'local.md', reason: 'truncated-to-budget', omittedCharacters: 7 }],
    } } });
    await fixture.whenStable();
    fixture.detectChanges();
  });
  afterEach(() => http.verify());

  it('shows effective overrides and renders examples as text', () => {
    const card = fixture.nativeElement.querySelector('.rule-card') as HTMLElement;
    expect(card.textContent).toContain('Disabled · low · generic · project');
    expect(card.textContent).toContain('Migration in progress.');
    expect(card.textContent).toContain('<script>bad</script>');
    expect(card.querySelector('script')).toBeNull();
  });

  it('shows included prompt text and budget omissions without claiming a completed-run prompt', () => {
    fixture.componentInstance.section.set('prompt');
    fixture.detectChanges();
    const content = fixture.nativeElement.textContent as string;
    expect(content).toContain('all technologies');
    expect(content).toContain('not a saved prompt from a completed run');
    expect(content).toContain('5 / 5 characters');
    expect(content).toContain('truncated-to-budget');
    expect(content).not.toContain('first second');
    expect(fixture.nativeElement.querySelector('pre').textContent).toBe('first');
  });

  it('opens canonical metric and effective rule definitions from domain references', async () => {
    fixture.componentInstance.section.set('domains');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-metric-id="grade"]').click();
    fixture.detectChanges();
    expect(fixture.componentInstance.section()).toBe('metrics');
    expect(fixture.nativeElement.querySelector('[data-metric-id="grade"]').open).toBeTrue();
    await fixture.whenStable();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('[data-metric-id="grade"] > summary'));
    fixture.componentInstance.section.set('domains');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-rule-id="QS-GN-003"]').click();
    fixture.detectChanges();
    expect(fixture.componentInstance.section()).toBe('rules');
    expect(fixture.nativeElement.querySelectorAll('.rule-card').length).toBe(1);
    expect(fixture.nativeElement.querySelector('.rule-card').textContent).toContain('Disabled · low');
    expect(fixture.nativeElement.querySelector('.rule-card').open).toBeTrue();
    await fixture.whenStable();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.rule-card > summary'));
  });

  it('explains the separate hotspot and coverage-based risk formulas', () => {
    fixture.componentInstance.section.set('metrics');
    fixture.detectChanges();
    const content = fixture.nativeElement.textContent as string;
    expect(content).toContain('log10(changes + 1)');
    expect(content).toContain('0.4 × (100 − code grade)');
    expect(content).toContain('not a failure probability');
  });

  it('shows rule effectiveness and trend from the repository report', async () => {
    const loading = fixture.componentInstance.showEffectiveness();
    http.expectOne('/api/repos/default/rules/effectiveness').flush({
      generatedAt: '2026-09-28T12:00:00Z', costCurrency: 'USD', unattributedCost: 2, unpricedRuns: 1,
      worstOffenders: [{ ruleId: 'QS-GN-003', hits: 2, accepted: 0, dismissed: 1, falsePositives: 1,
        resolved: 0, cost: 0.5, unpricedRuns: 0, falsePositiveRate: 0.5, trend: [] }],
      rules: [{ ruleId: 'QS-GN-003', hits: 2, accepted: 0, dismissed: 1, falsePositives: 1,
        resolved: 0, cost: 0.5, unpricedRuns: 0, falsePositiveRate: 0.5,
        trend: [{ day: '2026-09-28', hits: 2, accepted: 0, dismissed: 1, falsePositives: 1, resolved: 0, cost: 0.5 }] }],
    });
    await loading;
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Unattributed cost');
    expect(fixture.nativeElement.textContent).toContain('false positives');
    expect(fixture.nativeElement.querySelectorAll('.effectiveness-table tbody tr').length).toBe(1);
  });
});
