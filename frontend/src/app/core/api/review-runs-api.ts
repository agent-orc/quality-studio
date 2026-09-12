import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiContext } from './api-context';
import {
  QualityRunReport, QualityRunTrendPage, QuotaReport, ReviewKind, ReviewModelCatalog,
  ReviewModelRecommendation, ReviewPreflight, ReviewRun, ReviewRunCompareResult, ReviewRunRetention,
  RunReportFormat, StartReviewRequest, UsageReport,
} from '../models/contracts';

const POLL_INTERVAL_MS = 1_500;
const TERMINAL_STATES = ['done', 'failed', 'cancelled', 'capped'];
const ACTIVE_STATES = ['queued', 'running'];

export const emptyUsageReport = (): UsageReport => ({
  generatedAt: '', runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0,
  reasoningOutputTokens: 0, durationMs: 0, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [],
});

/**
 * Review runs and the numbers they produce: the model catalog, preflight, run control, canonical
 * reports, comparison, token usage, and provider quotas.
 *
 * Work that a settled run invalidates elsewhere (tree, dashboard, open file) is reported through
 * `onRunsSettled` rather than reached for directly, so this service stays free of the other
 * domains. `onUnreachable` lets the shell own the reconnect probe in one place.
 */
@Injectable({ providedIn: 'root' })
export class ReviewRunsApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ApiContext);

  readonly runs = signal<ReviewRun[]>([]);
  readonly reviewError = signal('');
  readonly modelCatalog = signal<ReviewModelCatalog>({
    schemaVersion: 1, policyVersion: '', evidenceAsOfDate: '',
    sourceRepository: 'agent-orc/token-economy', sourceCommit: '', thinkingLevels: [], models: [],
  });
  readonly usage = signal<UsageReport>(emptyUsageReport());
  readonly quotas = signal<QuotaReport>({ at: '', ttlSeconds: 0, providers: [] });

  /** Called once after a run reaches a terminal state, so dependent views can refresh. */
  onRunsSettled: (() => Promise<void>) | null = null;
  /** Called when a request found no API at all. */
  onUnreachable: (() => void) | null = null;

  private pollTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      if (this.pollTimer !== null) clearTimeout(this.pollTimer);
      this.pollTimer = null;
    });
  }

  async start(request: StartReviewRequest): Promise<ReviewRun> {
    this.reviewError.set('');
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.context.repositoryApiBase()}/review`, request));
      this.runs.update(runs => [run, ...runs.filter(candidate => candidate.id !== run.id)]);
      this.schedulePoll();
      console.info(JSON.stringify({ event: 'qs.review.queued', runId: run.id, path: run.path, kind: run.kind, fileCount: run.totalFiles }));
      return run;
    } catch (error) {
      this.reviewError.set(this.context.errorMessage(error));
      throw error;
    }
  }

  async estimate(request: StartReviewRequest): Promise<ReviewPreflight> {
    this.reviewError.set('');
    try {
      return await firstValueFrom(this.http.post<ReviewPreflight>(`${this.context.repositoryApiBase()}/review/estimate`, request));
    } catch (error) {
      this.reviewError.set(this.context.errorMessage(error));
      throw error;
    }
  }

  async loadModelCatalog(): Promise<void> {
    try {
      this.modelCatalog.set(await firstValueFrom(this.http.get<ReviewModelCatalog>('/api/models')));
    } catch (error) {
      console.warn(JSON.stringify({ event: 'qs.models.unavailable', reason: this.context.errorMessage(error) }));
    }
  }

  async defaultModelRecommendation(kind: ReviewKind, level: string, files: number): Promise<ReviewModelRecommendation | null> {
    try {
      const params = { kind, level, files: String(files) };
      return await firstValueFrom(this.http.get<ReviewModelRecommendation>('/api/models/default', { params }));
    } catch {
      return null;
    }
  }

  async load(repositoryId = this.context.selectedRepositoryId()): Promise<void> {
    if (repositoryId !== this.context.selectedRepositoryId()) return;
    try {
      const before = new Map(this.runs().map(run => [run.id, run.state]));
      const result = await firstValueFrom(this.http.get<{ runs: ReviewRun[] }>(`${this.context.repositoryApiBase(repositoryId)}/review/runs`));
      if (repositoryId !== this.context.selectedRepositoryId()) return;
      this.runs.set(result.runs);
      const settled = result.runs.some(run =>
        TERMINAL_STATES.includes(run.state) && ACTIVE_STATES.includes(before.get(run.id) ?? ''));
      if (settled) {
        await this.onRunsSettled?.();
        await Promise.all([this.loadUsage(), this.loadQuotas()]);
      }
      if (this.anyActive()) this.schedulePoll();
    } catch (error) {
      this.reviewError.set(this.context.errorMessage(error));
      // A failed poll used to end the poll loop for the session. Keep watching instead: unfinished
      // runs stay polled, and an unreachable API is retried until it answers again.
      if (this.anyActive()) this.schedulePoll();
      if (this.context.unreachable(error)) this.onUnreachable?.();
    }
  }

  async cancel(id: string): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.delete<ReviewRun>(`${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}`));
      this.replace(run);
      await this.onRunsSettled?.();
      this.schedulePoll();
    } catch (error) {
      this.reviewError.set(this.context.errorMessage(error));
    }
  }

  async pause(id: string): Promise<void> {
    try {
      this.replace(await firstValueFrom(this.http.post<ReviewRun>(
        `${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pause`, {})));
    } catch (error) {
      this.reviewError.set(this.context.errorMessage(error));
    }
  }

  async resume(id: string, cap: { tokenCap?: number | null; costCap?: number | null } = {}): Promise<void> {
    try {
      this.replace(await firstValueFrom(this.http.post<ReviewRun>(
        `${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/resume`, cap)));
      this.schedulePoll();
    } catch (error) {
      this.reviewError.set(this.context.errorMessage(error));
    }
  }

  async loadReport(id: string): Promise<QualityRunReport> {
    return await firstValueFrom(this.http.get<QualityRunReport>(
      `${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/report`,
      { params: { format: 'json' } }));
  }

  async loadTrend(kind: ReviewKind, scopeUnitId: string, level: string, cursor?: string): Promise<QualityRunTrendPage> {
    const params: Record<string, string> = { kind, scopeUnitId, level, limit: '30' };
    if (cursor) params['cursor'] = cursor;
    return await firstValueFrom(this.http.get<QualityRunTrendPage>(
      `${this.context.repositoryApiBase()}/review/runs/trend`, { params }));
  }

  async compare(baselineId: string, candidateId: string): Promise<ReviewRunCompareResult> {
    return await firstValueFrom(this.http.get<ReviewRunCompareResult>(
      `${this.context.repositoryApiBase()}/review/runs/compare`, { params: { baselineId, candidateId } }));
  }

  async loadRetention(): Promise<ReviewRunRetention> {
    return await firstValueFrom(this.http.get<ReviewRunRetention>(`${this.context.repositoryApiBase()}/review/runs/retention`));
  }

  async loadPinnedRunIds(): Promise<string[]> {
    const result = await firstValueFrom(this.http.get<{ pinnedRunIds: string[] }>(`${this.context.repositoryApiBase()}/review/runs/pins`));
    return result.pinnedRunIds;
  }

  async pin(id: string): Promise<string[]> {
    const result = await firstValueFrom(this.http.post<{ pinnedRunIds: string[] }>(
      `${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pin`, {}));
    return result.pinnedRunIds;
  }

  async unpin(id: string): Promise<string[]> {
    const result = await firstValueFrom(this.http.delete<{ pinnedRunIds: string[] }>(
      `${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pin`));
    return result.pinnedRunIds;
  }

  reportUrl(id: string, format: RunReportFormat): string {
    return `${this.context.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/report?format=${format}`;
  }

  reportFileName(id: string, format: RunReportFormat): string {
    const extension = format === 'markdown' ? 'md' : format === 'sarif' ? 'sarif' : format;
    return `quality-run-${id}.${extension}`;
  }

  repositoryReportUrl(format: RunReportFormat = 'html'): string {
    return `${this.context.repositoryApiBase()}/report?format=${format}`;
  }

  async loadUsage(since?: string, kind?: ReviewKind, repositoryId = this.context.selectedRepositoryId()): Promise<void> {
    if (repositoryId !== this.context.selectedRepositoryId()) return;
    try {
      const params: Record<string, string> = {};
      if (since) params['since'] = since;
      if (kind) params['kind'] = kind;
      const usage = await firstValueFrom(this.http.get<UsageReport>(`${this.context.repositoryApiBase(repositoryId)}/usage`, { params }));
      if (repositoryId === this.context.selectedRepositoryId()) this.usage.set(usage);
    } catch (error) {
      if (repositoryId === this.context.selectedRepositoryId()) {
        this.usage.set(emptyUsageReport());
        if (this.context.unreachable(error)) this.onUnreachable?.();
        console.warn(JSON.stringify({ event: 'qs.usage.unavailable', reason: this.context.errorMessage(error) }));
      }
    }
  }

  async loadQuotas(): Promise<void> {
    try {
      this.quotas.set(await firstValueFrom(this.http.get<QuotaReport>('/api/quotas')));
    } catch (error) {
      this.quotas.set({ at: new Date().toISOString(), ttlSeconds: 0, providers: [] });
      console.warn(JSON.stringify({ event: 'qs.quotas.unavailable', reason: this.context.errorMessage(error) }));
    }
  }

  private anyActive(): boolean {
    return this.runs().some(run => ACTIVE_STATES.includes(run.state));
  }

  private replace(run: ReviewRun): void {
    this.runs.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
  }

  private schedulePoll(): void {
    if (this.pollTimer !== null) return;
    this.pollTimer = setTimeout(() => {
      this.pollTimer = null;
      void this.load();
    }, POLL_INTERVAL_MS);
  }
}
