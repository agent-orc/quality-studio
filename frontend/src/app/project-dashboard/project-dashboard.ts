import { ChangeDetectionStrategy, Component, computed, inject, output } from '@angular/core';
import { formatBytes } from '../format';
import { FindingSeverity, ProjectDistributionBucket, QualityApi } from '../quality-api';

@Component({
  selector: 'qs-project-dashboard',
  templateUrl: './project-dashboard.html',
  styleUrl: './project-dashboard.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDashboardView {
  readonly api = inject(QualityApi);
  readonly nodeOpen = output<string>();
  readonly severities: FindingSeverity[] = ['critical', 'high', 'medium', 'low', 'info'];
  readonly states = ['fresh', 'stale', 'missing'] as const;
  readonly maxHotspotRisk = computed(() => Math.max(1, ...((this.api.project()?.hotspots ?? []).map(item => item.risk))));

  open(path: string | null | undefined): void {
    if (path) this.nodeOpen.emit(path);
  }

  securityPath(): string {
    return this.api.security()?.findings[0]?.path
      ?? this.api.project()?.reviewCoverage.path
      ?? this.api.tree()[0]?.path
      ?? '.';
  }

  metricPath(): string {
    return this.api.project()?.metrics.languages[0]?.path
      ?? this.api.project()?.reviewCoverage.path
      ?? this.api.tree()[0]?.path
      ?? '.';
  }

  distributionMax(buckets: ProjectDistributionBucket[]): number {
    return Math.max(1, ...buckets.map(bucket => bucket.count));
  }

  bytes(value: number): string { return formatBytes(value); }
}
