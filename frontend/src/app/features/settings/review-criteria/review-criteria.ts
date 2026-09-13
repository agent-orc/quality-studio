import { afterNextRender, ChangeDetectionStrategy, Component, ElementRef, Injector, computed, effect, inject, signal, untracked } from '@angular/core';
import { ReviewPolicyApi } from '../../../core/api/review-policy-api';
import { ReviewKind } from '../../../core/models/contracts';
import { QualityDomains } from './quality-domains';
import { REVIEW_METHODOLOGY } from './review-methodology.generated';

@Component({
  selector: 'qs-review-criteria',
  imports: [QualityDomains],
  templateUrl: './review-criteria.html',
  styleUrl: './review-criteria.css',
  providers: [ReviewPolicyApi],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewCriteria {
  readonly policy = inject(ReviewPolicyApi);
  readonly methodology = REVIEW_METHODOLOGY;
  readonly section = signal<'rules' | 'prompt' | 'metrics' | 'domains'>('rules');
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  readonly selectedRule = signal<string | null>(null);
  readonly selectedMetric = signal<string | null>(null);
  readonly kind = signal<ReviewKind>('code');
  readonly technology = signal('all');
  readonly query = signal('');
  readonly inputPreview = computed(() => this.policy.inputPreview()?.kinds[this.kind()]);
  readonly ruleRows = computed(() => {
    const catalogue = this.policy.catalogue();
    const traces = new Map(catalogue?.traces.map(trace => [trace.id, trace]));
    const query = this.query().trim().toLowerCase();
    return (catalogue?.rules ?? [])
      .filter(rule => rule.kinds.includes(this.kind())
        && (this.technology() === 'all' || rule.technology === this.technology() || rule.technology === 'generic')
        && (!query || `${rule.id} ${rule.title} ${rule.statement}`.toLowerCase().includes(query)))
      .map(rule => ({ rule, trace: traces.get(rule.id) }));
  });

  constructor() {
    effect(() => {
      const repositoryId = this.policy.repositoryId();
      untracked(() => { void this.policy.load(repositoryId); });
    });
  }

  showMetric(id: string): void {
    if (!this.methodology.metrics.some(metric => metric.id === id)) return;
    this.selectedMetric.set(id);
    this.section.set('metrics');
    this.focusReference('data-metric-id', id);
  }

  showRule(id: string): void {
    const rule = this.policy.catalogue()?.rules.find(item => item.id === id);
    this.kind.set(rule?.kinds[0] ?? 'code');
    this.technology.set('all');
    this.query.set(id);
    this.selectedRule.set(id);
    this.section.set('rules');
    this.focusReference('data-rule-id', id);
  }

  private focusReference(attribute: 'data-metric-id' | 'data-rule-id', id: string): void {
    afterNextRender(() => {
      const target = Array.from(this.element.nativeElement.querySelectorAll('details'))
        .find(card => card.getAttribute(attribute) === id);
      target?.querySelector<HTMLElement>('summary')?.focus();
    }, { injector: this.injector });
  }

  setKind(value: string): void {
    if (value === 'code' || value === 'security' || value === 'performance') this.kind.set(value);
  }
}
