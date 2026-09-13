import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityDomains } from './quality-domains';

describe('QualityDomains', () => {
  let fixture: ComponentFixture<QualityDomains>;
  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [QualityDomains] }).compileComponents();
    fixture = TestBed.createComponent(QualityDomains);
    fixture.detectChanges();
  });

  it('distinguishes existing metrics, review rules and planned work without claiming applicability', () => {
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelectorAll('.domain-card').length).toBe(13);
    expect(element.querySelectorAll('[data-check-status="implemented-metric"]').length).toBe(6);
    const planned = element.querySelector('[data-domain-id="payments"]')!;
    expect(planned.textContent).toContain('Planned');
    expect(planned.querySelector('[data-metric-id]')).toBeNull();
    expect(element.textContent).toContain('missing means unknown');
    expect(element.textContent).toContain('values are never inherited');
    expect(element.textContent).toContain('No YAML reader or runtime filter is active');
  });

  it('keeps review prioritization separate from performance and labels field observations planned', () => {
    const element: HTMLElement = fixture.nativeElement;
    const performance = element.querySelector('[data-domain-id="performance"]')!;
    expect(performance.querySelector('[data-metric-id="risk-view"]')).toBeNull();
    expect(performance.querySelector('[data-check-id="performance-field-web-vitals"]')?.getAttribute('data-check-status')).toBe('planned');
    expect(performance.textContent).toContain('Missing field data is unavailable');
    expect(element.querySelector('[data-domain-id="review-prioritization"] [data-metric-id="risk-view"]')).not.toBeNull();
  });

  it('filters by implementation status without presenting planned checks as results', () => {
    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    select.value = 'planned';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.domain-check').length).toBe(8);
    expect(fixture.nativeElement.querySelector('[data-check-status="implemented-metric"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-domain-id="payments"]')).not.toBeNull();
    select.value = 'implemented-metric';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.domain-check').length).toBe(6);
    expect(fixture.nativeElement.querySelector('[data-check-status="planned"]')).toBeNull();
  });

  it('emits explicit metric and rule navigation without changing policy', () => {
    const metric = jasmine.createSpy('metric');
    const rule = jasmine.createSpy('rule');
    fixture.componentInstance.openMetric.subscribe(metric);
    fixture.componentInstance.openRule.subscribe(rule);
    fixture.nativeElement.querySelector('[data-metric-id="grade"]').click();
    fixture.nativeElement.querySelector('[data-rule-id="QS-GN-005"]').click();
    expect(metric).toHaveBeenCalledOnceWith('grade');
    expect(rule).toHaveBeenCalledOnceWith('QS-GN-005');
  });
});
