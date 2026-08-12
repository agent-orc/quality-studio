import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { formatDateTime } from '../format';
import {
  QualityApi, QualityRunComparison, QualityRunComparisonSnapshot, QualityRunFinding,
  QualityRunFindingDeltaCategory, QualityRunTrendPoint,
} from '../quality-api';

@Component({
  selector: 'qs-run-comparison',
  imports: [],
  templateUrl: './run-comparison.html',
  styleUrl: './run-comparison.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunComparison {
  readonly api = inject(QualityApi);
  readonly points = input.required<QualityRunTrendPoint[]>();
  readonly selectedRunId = input<string | null>(null);
  readonly open = signal(false);
  readonly comparison = signal<QualityRunComparison | null>(null);
  readonly baselineId = signal('');
  readonly candidateId = signal('');
  readonly loading = signal(false);
  readonly error = signal('');
  readonly categories: QualityRunFindingDeltaCategory[] = ['new', 'dispositionChanged', 'resolved', 'unchanged'];
  readonly comparableRuns = computed(() => this.points().filter(point => point.comparable));
  readonly baselineOptions = computed(() => {
    const candidate = this.comparableRuns().find(point => point.runId === this.candidateId());
    if (!candidate) return [];
    const candidateTime = new Date(candidate.finishedAt).getTime();
    return this.comparableRuns().filter(point => point.runId !== candidate.runId
      && new Date(point.finishedAt).getTime() <= candidateTime);
  });

  async openComparison(): Promise<void> {
    this.open.set(true);
    this.comparison.set(null);
    this.error.set('');
    const selected = this.selectedRunId();
    const candidate = this.comparableRuns().find(point => point.runId === selected)
      ?? this.comparableRuns()[0];
    this.candidateId.set(candidate?.runId ?? '');
    this.selectDefaultBaseline();
    if (this.baselineId()) await this.compare();
    else this.error.set('A second earlier complete run is required for comparison.');
  }

  close(): void {
    this.open.set(false);
    this.comparison.set(null);
    this.baselineId.set('');
    this.candidateId.set('');
    this.loading.set(false);
    this.error.set('');
  }

  changeCandidate(runId: string): void {
    this.candidateId.set(runId);
    this.comparison.set(null);
    this.error.set('');
    this.selectDefaultBaseline();
  }

  changeBaseline(runId: string): void {
    this.baselineId.set(runId);
    this.comparison.set(null);
    this.error.set('');
  }

  async compare(): Promise<void> {
    const baselineId = this.baselineId();
    const candidateId = this.candidateId();
    if (!baselineId || !candidateId) {
      this.error.set('Choose one baseline and one candidate run.');
      return;
    }
    this.loading.set(true);
    this.error.set('');
    try {
      this.comparison.set(await this.api.compareRuns(baselineId, candidateId));
    } catch (error) {
      this.comparison.set(null);
      this.error.set(this.api.errorMessage(error));
    } finally {
      this.loading.set(false);
    }
  }

  scannedAt(value: string): string { return formatDateTime(value); }

  delta(before: number | null, after: number | null): string {
    if (before === null || after === null) return 'unavailable';
    const difference = after - before;
    return `${difference > 0 ? '+' : ''}${difference}`;
  }

  tokens(snapshot: QualityRunComparisonSnapshot): string {
    if (snapshot.inputTokens === null || snapshot.outputTokens === null) return 'unavailable';
    return this.formatTokens(snapshot.inputTokens + snapshot.outputTokens);
  }

  duration(value: number): string { return value >= 1000 ? `${(value / 1000).toFixed(1)}s` : `${value}ms`; }

  cost(snapshot: QualityRunComparisonSnapshot): string {
    return snapshot.cost === null ? 'unavailable' : `${snapshot.cost.toFixed(4)} ${snapshot.currency ?? 'USD'}`;
  }

  categoryLabel(category: QualityRunFindingDeltaCategory): string {
    return category === 'dispositionChanged' ? 'Disposition changed' : category;
  }

  findingLocation(finding: QualityRunFinding): string {
    const location = finding.locations[0];
    if (!location) return 'Location unavailable';
    return `${location.path}${location.startLine === null ? '' : `:${location.startLine}`}`;
  }

  private selectDefaultBaseline(): void {
    this.baselineId.set(this.baselineOptions()[0]?.runId ?? '');
  }

  private formatTokens(value: number): string {
    return value >= 1_000_000 ? `${(value / 1_000_000).toFixed(1)}m tok`
      : value >= 1_000 ? `${(value / 1_000).toFixed(1)}k tok` : `${value} tok`;
  }
}
