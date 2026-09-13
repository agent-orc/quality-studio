import { ChangeDetectionStrategy, Component, computed, output, signal } from '@angular/core';
import { QUALITY_DOMAINS } from './quality-domains.generated';
import { REVIEW_METHODOLOGY } from './review-methodology.generated';

@Component({
  selector: 'qs-quality-domains',
  templateUrl: './quality-domains.html',
  styleUrl: './quality-domains.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QualityDomains {
  readonly openMetric = output<string>();
  readonly openRule = output<string>();
  readonly domains = QUALITY_DOMAINS.domains.map(domain => ({
    ...domain,
    checks: domain.checks.map(check => ({
      ...check,
      sources: check.sourceIds.map(id => QUALITY_DOMAINS.sources.find(source => source.id === id)!),
      metrics: check.metricIds.map(id => REVIEW_METHODOLOGY.metrics.find(metric => metric.id === id)!),
      scopeLabel: check.applicability.subjectScope === 'project' ? 'Project properties' : 'Component’s own properties',
      requirements: [
        ...(check.applicability.allOf.length ? [`All true: ${check.applicability.allOf.join(', ')}`] : []),
        ...(check.applicability.anyOf.length ? [`At least one true: ${check.applicability.anyOf.join(', ')}`] : []),
        ...(check.applicability.noneOf.length ? [`All false: ${check.applicability.noneOf.join(', ')}`] : []),
      ],
    })),
  }));
  readonly status = signal('all');
  readonly visibleDomains = computed(() => this.domains
    .map(domain => ({ ...domain, checks: domain.checks.filter(check => this.status() === 'all' || check.status === this.status()) }))
    .filter(domain => domain.checks.length > 0));
  readonly statusLabels: Record<string, string> = {
    'implemented-metric': 'Implemented metric',
    'review-rule': 'Review rule',
    planned: 'Planned',
  };
  readonly methodLabels: Record<string, string> = {
    measurement: 'Measurement', heuristic: 'Prioritization heuristic', review: 'Reviewer judgment',
  };
}
