import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { UsageAggregate, UsageEntry } from '../contracts';
import { formatCost, formatModelSource, formatPriceStatus } from '../format';
import { Modal } from '../dialog/modal';
import { QualityApi } from '../quality-api';

@Component({
  selector: 'qs-usage-history',
  imports: [Modal],
  templateUrl: './usage-history.html',
  styleUrl: './usage-history.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsageHistory {
  readonly api = inject(QualityApi);
  readonly closed = output<void>();
  readonly expandedEntry = signal<number | null>(null);
  readonly totalTokens = computed(() => this.api.usage().inputTokens + this.api.usage().outputTokens);
  readonly durableRuns = computed(() => this.api.usage().byReviewRun?.length ?? 0);
  readonly totalCost = computed(() => formatCost(this.api.usage().estimatedCost, this.api.usage().costCurrency));
  readonly unpricedRuns = computed(() => this.api.usage().unpricedRuns ?? 0);

  toggleEntry(index: number): void {
    this.expandedEntry.update(current => current === index ? null : index);
  }

  tokens(item: UsageAggregate | UsageEntry): number {
    const usage = 'tokens' in item ? item.tokens : item;
    return (usage.inputTokens ?? 0) + (usage.outputTokens ?? 0);
  }

  barWidth(item: UsageAggregate, items: UsageAggregate[]): number {
    const maximum = Math.max(1, ...items.map(candidate => this.tokens(candidate)));
    return Math.max(2, this.tokens(item) / maximum * 100);
  }

  /** The cost of one ledger entry, or an honest statement that it could not be priced. */
  entryCost(entry: UsageEntry): string {
    if (!entry.cost || entry.cost.total === null) return 'unpriced';
    return formatCost(entry.cost.total, entry.cost.currency);
  }

  entryCostDetail(entry: UsageEntry): string {
    if (!entry.cost) return 'No cost was recorded for this entry.';
    return entry.cost.total === null
      ? `Unpriced: ${formatPriceStatus(entry.cost.status)}.`
      : `${formatCost(entry.cost.total, entry.cost.currency)} (${formatPriceStatus(entry.cost.status)})`;
  }

  entryModelSource(entry: UsageEntry): string { return formatModelSource(entry.modelSource); }

  formatNumber(value: number | null): string {
    return value === null ? 'Unavailable' : new Intl.NumberFormat('en-US').format(value);
  }

  formatDate(value: string): string {
    return new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }

  formatDay(value: string): string {
    return new Intl.DateTimeFormat('en', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      timeZone: 'UTC',
    }).format(new Date(`${value}T00:00:00Z`));
  }

  formatDuration(value: number): string {
    if (value < 1_000) return `${value} ms`;
    if (value < 60_000) return `${(value / 1_000).toFixed(1)} s`;
    return `${(value / 60_000).toFixed(1)} min`;
  }
}
