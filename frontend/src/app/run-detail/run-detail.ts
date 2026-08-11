import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { formatDateTime } from '../format';
import { QualityApi, QualityRunReport, QualityRunTrendPage } from '../quality-api';

@Component({
  selector: 'qs-run-detail',
  templateUrl: './run-detail.html',
  styleUrl: './run-detail.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunDetail {
  readonly api = inject(QualityApi);
  readonly report = input.required<QualityRunReport>();
  readonly trend = input<QualityRunTrendPage | null>(null);
  readonly close = output<void>();
  readonly findings = computed(() => {
    const findings = this.report()
      .observations.flatMap((observation) => observation.findings)
      .filter((finding) => finding.state !== 'resolved');
    return [
      ...new Map(findings.map((finding) => [finding.fingerprint ?? finding.id, finding])).values(),
    ];
  });

  scannedAt(value: string): string {
    return formatDateTime(value);
  }

  formatTokens(value: number | null | undefined): string {
    if (value === null || value === undefined) return 'unavailable';
    return value >= 1_000_000
      ? `${(value / 1_000_000).toFixed(1)}m tok`
      : value >= 1_000
        ? `${(value / 1_000).toFixed(1)}k tok`
        : `${value} tok`;
  }

  formatDuration(durationMs: number): string {
    if (durationMs < 1_000) return `${durationMs} ms`;
    if (durationMs < 60_000) return `${(durationMs / 1_000).toFixed(1)} s`;
    return `${(durationMs / 60_000).toFixed(1)} min`;
  }

  formatCost(cost: number | null, currency: string | null): string {
    return cost === null ? 'cost unavailable' : `${cost.toFixed(4)} ${currency ?? 'USD'}`;
  }
}
