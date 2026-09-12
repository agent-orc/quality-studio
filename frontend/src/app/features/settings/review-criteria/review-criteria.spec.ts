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

  it('explains the separate hotspot and coverage-based risk formulas', () => {
    fixture.componentInstance.section.set('metrics');
    fixture.detectChanges();
    const content = fixture.nativeElement.textContent as string;
    expect(content).toContain('log10(changes + 1)');
    expect(content).toContain('0.4 × (100 − code grade)');
    expect(content).toContain('not a failure probability');
  });
});
