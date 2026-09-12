import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { ReviewPolicyApi } from '../../../core/api/review-policy-api';
import { ReviewKind } from '../../../core/models/contracts';
import { REVIEW_METHODOLOGY } from './review-methodology.generated';

@Component({
  selector: 'qs-review-criteria',
  templateUrl: './review-criteria.html',
  styleUrl: './review-criteria.css',
  providers: [ReviewPolicyApi],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewCriteria {
  readonly policy = inject(ReviewPolicyApi);
  readonly methodology = REVIEW_METHODOLOGY;
  readonly section = signal<'rules' | 'prompt' | 'metrics'>('rules');
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

  setKind(value: string): void {
    if (value === 'code' || value === 'security' || value === 'performance') this.kind.set(value);
  }
}
