import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, computed, inject, output, signal, viewChild } from '@angular/core';
import { QualityApi, UsageAggregate, UsageEntry } from '../quality-api';

@Component({
  selector: 'qs-usage-history',
  templateUrl: './usage-history.html',
  styleUrl: './usage-history.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:keydown)': 'onDocumentKeydown($event)',
  },
})
export class UsageHistory implements AfterViewInit {
  readonly api = inject(QualityApi);
  readonly closed = output<void>();
  readonly expandedEntry = signal<number | null>(null);
  readonly dialog = viewChild.required<ElementRef<HTMLElement>>('dialog');
  readonly totalTokens = computed(() => this.api.usage().inputTokens + this.api.usage().outputTokens);
  readonly durableRuns = computed(() => this.api.usage().byReviewRun?.length ?? 0);

  ngAfterViewInit(): void {
    queueMicrotask(() => this.dialog().nativeElement.focus());
  }

  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closed.emit();
      return;
    }
    if (event.key !== 'Tab') return;

    const dialog = this.dialog().nativeElement;
    const focusable = Array.from(dialog.querySelectorAll<HTMLElement>(
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), ' +
      'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'));
    if (!focusable.length) {
      event.preventDefault();
      dialog.focus();
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    if (event.shiftKey && (active === first || active === dialog || !dialog.contains(active))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (active === last || !dialog.contains(active))) {
      event.preventDefault();
      first.focus();
    }
  }

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
