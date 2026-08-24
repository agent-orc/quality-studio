import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { formatDateTime } from '../format';
import {
  QualityApi,
  ReviewHistoryDetail,
  ReviewHistorySummary,
  ReviewKind,
  ReviewRunDiffResponse,
  ReviewRunState,
} from '../quality-api';

type ReviewHistoryOutcome = ReviewRunState | 'legacy-usage-only' | '';

@Component({
  selector: 'qs-review-history',
  templateUrl: './review-history.html',
  styleUrl: './review-history.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewHistory {
  readonly api = inject(QualityApi);
  private readonly http = inject(HttpClient);
  readonly open = signal(false);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly rows = signal<ReviewHistorySummary[]>([]);
  readonly nextCursor = signal<string | null>(null);
  readonly kind = signal<ReviewKind | ''>('');
  readonly path = signal('');
  readonly outcome = signal<ReviewHistoryOutcome>('');
  readonly detail = signal<ReviewHistoryDetail | null>(null);
  readonly selected = signal<ReviewHistorySummary[]>([]);
  readonly diff = signal<ReviewRunDiffResponse | null>(null);
  readonly compareStatus = signal('');
  readonly compareReady = computed(() => this.selected().length === 2);
  private loaded = false;
  private restoring = false;

  constructor() {
    if (typeof location === 'undefined') return;
    const params = new URLSearchParams(location.search);
    if (params.has('history') || params.has('compare')) {
      this.open.set(true);
      queueMicrotask(() => void this.load(false, true));
    }
  }

  async toggle(): Promise<void> {
    this.open.update(open => !open);
    if (this.open() && !this.loaded) await this.load();
  }

  async applyFilters(): Promise<void> {
    this.selected.set([]);
    this.diff.set(null);
    this.compareStatus.set('');
    this.updateUrl();
    await this.load();
  }

  async loadMore(): Promise<void> {
    if (this.nextCursor()) await this.load(true);
  }

  async selectRun(run: ReviewHistorySummary): Promise<void> {
    if (run.errorCode || run.provenance === 'legacy-usage-only') return;
    this.error.set('');
    try {
      this.detail.set(await this.loadDetail(run.runId));
      this.updateUrl(run.runId);
    } catch (error) {
      this.error.set(this.api.errorMessage(error));
    }
  }

  closeDetail(): void {
    this.detail.set(null);
    this.updateUrl(null);
  }

  canSelect(run: ReviewHistorySummary): boolean {
    if (run.errorCode || run.provenance === 'legacy-usage-only') return false;
    const selected = this.selected();
    if (selected.some(candidate => candidate.runId === run.runId) || selected.length === 0) return true;
    if (selected.length >= 2) return false;
    const first = selected[0];
    return first.repositoryId === run.repositoryId && first.kind === run.kind && first.path === run.path;
  }

  async toggleCompare(run: ReviewHistorySummary): Promise<void> {
    const current = this.selected();
    if (current.some(candidate => candidate.runId === run.runId)) {
      this.selected.set(current.filter(candidate => candidate.runId !== run.runId));
      this.diff.set(null);
      this.compareStatus.set('');
      this.updateUrl();
      return;
    }
    if (!this.canSelect(run)) {
      this.compareStatus.set('Choose runs with the same kind and root path.');
      return;
    }
    this.selected.set([...current, run]);
    this.updateUrl();
    if (this.selected().length === 2) await this.compare();
  }

  selectedRun(runId: string): boolean {
    return this.selected().some(run => run.runId === runId);
  }

  timestamp(value: string | null): string {
    return value ? formatDateTime(value) : 'unknown time';
  }

  signed(value: number | null, suffix = ''): string {
    if (value === null) return 'unavailable';
    return `${value > 0 ? '+' : ''}${value}${suffix}`;
  }

  private async load(append = false, restore = false): Promise<void> {
    if (this.loading()) return;
    this.loading.set(true);
    this.error.set('');
    try {
      const page = await this.loadPage({
        cursor: append ? this.nextCursor() ?? undefined : undefined,
        limit: 20,
        kind: this.kind(),
        path: this.path(),
        outcome: this.outcome(),
      });
      this.rows.set(append
        ? [...this.rows(), ...page.runs.filter(run => !this.rows().some(existing => existing.runId === run.runId))]
        : page.runs);
      this.nextCursor.set(page.nextCursor);
      this.loaded = true;
      if (restore && !this.restoring) await this.restoreUrlState();
    } catch (error) {
      this.error.set(this.api.errorMessage(error));
    } finally {
      this.loading.set(false);
    }
  }

  private async compare(): Promise<void> {
    const [before, after] = this.selected();
    if (!before || !after) return;
    this.compareStatus.set('Comparing…');
    try {
      this.diff.set(await this.loadDiff(after.runId, before.runId));
      this.compareStatus.set('');
    } catch (error) {
      this.diff.set(null);
      this.compareStatus.set(this.api.errorMessage(error));
    }
  }

  private async restoreUrlState(): Promise<void> {
    if (typeof location === 'undefined') return;
    this.restoring = true;
    try {
      const params = new URLSearchParams(location.search);
      const detailId = params.get('history');
      if (detailId) {
        try { this.detail.set(await this.loadDetail(detailId)); }
        catch (error) { this.error.set(this.api.errorMessage(error)); }
      }
      const ids = (params.get('compare') ?? '').split(',').filter(Boolean).slice(0, 2);
      const selected: ReviewHistorySummary[] = [];
      for (const id of ids) {
        const row = this.rows().find(candidate => candidate.runId === id);
        if (row) { selected.push(row); continue; }
        try { selected.push(this.summary(await this.loadDetail(id))); }
        catch (error) { this.error.set(this.api.errorMessage(error)); }
      }
      if (selected.length === 2 && selected[0].kind === selected[1].kind && selected[0].path === selected[1].path) {
        this.selected.set(selected);
        await this.compare();
      }
    } finally {
      this.restoring = false;
    }
  }

  private summary(detail: ReviewHistoryDetail): ReviewHistorySummary {
    return {
      runId: detail.run.runId,
      repositoryId: detail.run.repositoryId,
      createdAt: detail.run.createdAt,
      path: detail.run.subject.path,
      level: detail.run.level,
      kind: detail.run.kind,
      outcome: detail.attempt.outcome,
      complete: detail.attempt.complete,
      attempt: detail.attempt.attempt,
      startedAt: detail.attempt.startedAt,
      finishedAt: detail.attempt.finishedAt,
      operations: detail.operations.length,
      findings: detail.findings.length,
      quality: detail.attempt.quality,
    };
  }

  private updateUrl(detailId: string | null | undefined = undefined): void {
    if (typeof location === 'undefined') return;
    const params = new URLSearchParams(location.search);
    if (detailId !== undefined) {
      if (detailId) params.set('history', detailId); else params.delete('history');
    } else if (this.detail()) {
      params.set('history', this.detail()!.run.runId);
    }
    const ids = this.selected().map(run => run.runId);
    if (ids.length) params.set('compare', ids.join(',')); else params.delete('compare');
    history.replaceState(null, '', `?${params}`);
  }

  private async loadPage(query: {
    cursor?: string; limit?: number; kind?: ReviewKind | ''; path?: string; outcome?: ReviewHistoryOutcome;
  }): Promise<import('../quality-api').ReviewHistoryPage> {
    const params: Record<string, string> = {};
    if (query.cursor) params['cursor'] = query.cursor;
    if (query.limit) params['limit'] = String(query.limit);
    if (query.kind) params['kind'] = query.kind;
    if (query.path?.trim()) params['path'] = query.path.trim();
    if (query.outcome) params['outcome'] = query.outcome;
    return firstValueFrom(this.http.get<import('../quality-api').ReviewHistoryPage>(
      `${this.apiBase()}/review/history`, { params }));
  }

  private async loadDetail(runId: string): Promise<ReviewHistoryDetail> {
    return firstValueFrom(this.http.get<ReviewHistoryDetail>(
      `${this.apiBase()}/review/history/${encodeURIComponent(runId)}`));
  }

  private async loadDiff(afterRunId: string, beforeRunId: string): Promise<ReviewRunDiffResponse> {
    return firstValueFrom(this.http.get<ReviewRunDiffResponse>(
      `${this.apiBase()}/review/history/${encodeURIComponent(afterRunId)}/diff`,
      { params: { against: beforeRunId } }));
  }

  private apiBase(): string {
    return `/api/repos/${encodeURIComponent(this.api.selectedRepositoryId())}`;
  }
}
