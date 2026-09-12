import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';

import {
  QualityRunReport, QualityRunTrendPoint, ReviewKind, ReviewRun, ReviewRunCompareResult, RunReportFormat,
} from '../../../core/models/contracts';
import { ResumeCap, ResumeCapDialog } from '../../../shared/dialog/resume-cap-dialog';
import { formatCost, formatDateTime, formatModelSource, formatPriceStatus, formatTokenCount } from '../../../shared/utils/format';
import { QualityApi } from '../../../core/api/quality-api';
import { FlatNode } from '../../../shared/utils/tree-utils';

const TERMINAL_STATES = ['done', 'failed', 'cancelled', 'capped'];

/**
 * Run history for the selected scope: what each run cost and routed through, the canonical
 * snapshot of a finished run, its trend, and the comparison between two runs.
 *
 * It is deferred out of the review panel, which shows findings and does not need any of this until
 * the reader asks for it.
 */
@Component({
  selector: 'qs-run-history',
  imports: [ResumeCapDialog],
  templateUrl: './run-history.html',
  styleUrl: './run-history.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunHistory {
  readonly api = inject(QualityApi);
  readonly node = input<FlatNode | undefined>();
  readonly activeKind = input.required<ReviewKind>();

  readonly runDrawerOpen = signal(false);
  readonly selectedRunId = signal<string | null>(null);
  readonly runReport = signal<QualityRunReport | null>(null);
  readonly runTrend = signal<QualityRunTrendPoint[]>([]);
  readonly runTrendCursor = signal<string | null>(null);
  readonly runDetailLoading = signal(false);
  readonly runDetailError = signal('');
  readonly pinnedRunIds = signal<string[]>([]);
  readonly compareOpen = signal(false);
  readonly compareBaselineId = signal<string | null>(null);
  readonly compareCandidateId = signal<string | null>(null);
  readonly compareLoading = signal(false);
  readonly compareError = signal('');
  readonly compareResult = signal<ReviewRunCompareResult | null>(null);
  readonly resumingRun = signal<ReviewRun | null>(null);
  readonly runFormats: RunReportFormat[] = ['html', 'markdown', 'sarif', 'json'];

  readonly scopeRuns = computed(() => this.api.reviewRuns().filter(run =>
    run.path === this.node()?.path && run.kind === this.activeKind()));
  readonly selectedRun = computed(() => this.scopeRuns().find(run => run.id === this.selectedRunId()) ?? null);
  readonly comparableRuns = computed(() => this.scopeRuns().filter(run => TERMINAL_STATES.includes(run.state)));
  readonly runFindings = computed(() => (this.runReport()?.observations ?? [])
    .flatMap(observation => observation.findings)
    .filter(finding => finding.state !== 'resolved'));

  async toggleRunDrawer(): Promise<void> {
    const opening = !this.runDrawerOpen();
    this.runDrawerOpen.set(opening);
    if (opening) {
      try {
        this.pinnedRunIds.set(await this.api.loadPinnedRunIds());
      } catch {
        // Pin state is a convenience badge; the drawer stays usable without it.
      }
    }
  }

  async openRun(run: ReviewRun): Promise<void> {
    this.selectedRunId.set(run.id);
    this.runReport.set(null);
    this.runTrend.set([]);
    this.runTrendCursor.set(null);
    this.runDetailError.set('');
    if (!TERMINAL_STATES.includes(run.state)) return;
    this.runDetailLoading.set(true);
    try {
      const scopeUnitId = this.node()?.id;
      const [report, trend] = await Promise.all([
        this.api.loadRunReport(run.id),
        scopeUnitId ? this.api.loadRunTrend(run.kind, scopeUnitId, run.level) : Promise.resolve(null),
      ]);
      this.runReport.set(report);
      this.runTrend.set(trend?.points ?? []);
      this.runTrendCursor.set(trend?.nextCursor ?? null);
    } catch (error) {
      this.runDetailError.set(this.api.errorMessage(error));
    } finally {
      this.runDetailLoading.set(false);
    }
  }

  closeRun(): void {
    this.selectedRunId.set(null);
    this.runReport.set(null);
    this.runTrend.set([]);
    this.runTrendCursor.set(null);
    this.runDetailError.set('');
  }

  async loadOlderTrend(): Promise<void> {
    const cursor = this.runTrendCursor();
    const run = this.selectedRun();
    const scopeUnitId = this.node()?.id;
    if (!cursor || !run || !scopeUnitId) return;
    try {
      const page = await this.api.loadRunTrend(run.kind, scopeUnitId, run.level, cursor);
      this.runTrend.update(points => [...points, ...page.points]);
      this.runTrendCursor.set(page.nextCursor);
    } catch (error) {
      this.runDetailError.set(this.api.errorMessage(error));
    }
  }

  isPinned(runId: string): boolean { return this.pinnedRunIds().includes(runId); }

  async togglePin(runId: string): Promise<void> {
    try {
      this.pinnedRunIds.set(this.isPinned(runId) ? await this.api.unpinRun(runId) : await this.api.pinRun(runId));
    } catch (error) {
      this.runDetailError.set(this.api.errorMessage(error));
    }
  }

  openCompare(run: ReviewRun): void {
    const baseline = this.comparableRuns().find(candidate => candidate.id !== run.id) ?? null;
    this.compareOpen.set(true);
    this.compareBaselineId.set(baseline?.id ?? null);
    this.compareCandidateId.set(run.id);
    this.compareResult.set(null);
    this.compareError.set('');
    if (baseline) void this.runCompare();
  }

  closeCompare(): void {
    this.compareOpen.set(false);
    this.compareResult.set(null);
    this.compareError.set('');
  }

  async runCompare(): Promise<void> {
    const baselineId = this.compareBaselineId();
    const candidateId = this.compareCandidateId();
    if (!baselineId || !candidateId) return;
    this.compareLoading.set(true);
    this.compareError.set('');
    try {
      this.compareResult.set(await this.api.compareRuns(baselineId, candidateId));
    } catch (error) {
      this.compareError.set(this.api.errorMessage(error));
    } finally {
      this.compareLoading.set(false);
    }
  }

  async applyResumeCap(cap: ResumeCap): Promise<void> {
    const run = this.resumingRun();
    this.resumingRun.set(null);
    if (run) await this.api.resumeReview(run.id, cap);
  }

  reportUrl(runId: string, format: RunReportFormat): string { return this.api.runReportUrl(runId, format); }

  reportFileName(runId: string, format: RunReportFormat): string { return this.api.runReportFileName(runId, format); }

  trendScoreWidth(point: QualityRunTrendPoint): number { return point.score ?? 0; }

  runProgress(completed: number, total: number): number { return total ? completed / total * 100 : 0; }

  scannedAt(value: string): string { return formatDateTime(value); }

  formatTokens(value: number | null | undefined): string {
    if (value === null || value === undefined) return 'unavailable';
    return `${formatTokenCount(value)} tok`;
  }

  formatDuration(value: number): string { return value >= 1000 ? `${(value / 1000).toFixed(1)}s` : `${value}ms`; }

  spendLabel(run: ReviewRun): string {
    const spent = (run.usage.inputTokens ?? 0) + (run.usage.outputTokens ?? 0);
    return run.tokenCap !== null
      ? `${this.formatTokens(spent)} / ${this.formatTokens(run.tokenCap)}`
      : this.formatTokens(spent);
  }

  /** The money the run has actually spent, with its cap when one applies. "unpriced" stays unpriced. */
  costLabel(run: ReviewRun): string {
    const spent = formatCost(run.costSpent, run.currency);
    if (run.costSpent === null) return `unpriced (${formatPriceStatus(run.priceStatus)})`;
    return run.costCap !== null ? `${spent} / ${formatCost(run.costCap, run.currency)}` : spent;
  }

  modelLabel(run: ReviewRun): string { return run.model ?? 'runner default model'; }

  modelSourceLabel(run: ReviewRun): string { return formatModelSource(run.modelSource); }
}
