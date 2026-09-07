import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiContext } from './api-context';
import { describeFileError } from './api-errors';
import {
  CoverageFact, FileDocument, FileError, FindingStateMutationRequest, FindingSuppressionMutation,
  FindingSuppressionsResponse, HandoverRequest, HandoverResult, ReviewFinding, ReviewThread,
  ThreadMutationRequest,
} from './contracts';
import type { PreviewFixtures } from './preview-fixtures';

const unknownCoverage = (): CoverageFact => ({
  state: 'unknown', coveredLines: 0, totalLines: 0, coveredBranches: 0, totalBranches: 0,
  linePercent: null, branchPercent: null, commit: null, measuredAt: null, filesWithData: 0,
});

/**
 * The opened document and everything written against its findings: review threads, finding
 * dispositions, and the handover into an implementation task.
 *
 * A failed lookup produces a `fileError`, never someone else's content. Preview fixtures are served
 * only when the API cannot be reached at all, and the editor labels them.
 */
@Injectable({ providedIn: 'root' })
export class FindingsApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ApiContext);

  readonly file = signal<FileDocument | null>(null);
  readonly fileError = signal<FileError | null>(null);
  readonly loading = signal(false);
  readonly focusedThreadId = signal<string | null>(null);
  readonly handoverConfigured = signal(false);
  readonly handoverDryRun = signal(true);
  readonly findingSuppressions = signal<FindingSuppressionsResponse>({ schemaVersion: 1, revision: 0, rules: [] });

  /** Called after a mutation that changes review state elsewhere, such as a finding disposition. */
  onFindingsChanged: (() => Promise<void>) | null = null;

  private loadedPreviewFixtures: PreviewFixtures | null = null;

  async loadFile(path: string): Promise<void> {
    this.loading.set(true);
    try {
      const file = await firstValueFrom(this.http.get<FileDocument>(`${this.context.repositoryApiBase()}/file`, { params: { path } }));
      this.file.set(file);
      this.fileError.set(null);
      this.context.connectionState.set('live');
    } catch (error) {
      // A failed lookup never becomes someone else's source. The editor renders the reason.
      // Preview fixtures are reserved for the genuinely unreachable API and carry their own banner.
      if (this.context.unreachable(error)) {
        this.context.connectionState.set('preview');
        await this.showPreviewFile(path);
      } else {
        this.file.set(null);
        this.fileError.set(describeFileError(path, error));
        if (this.context.connectionState() === 'connecting') this.context.connectionState.set('offline');
      }
      console.warn(JSON.stringify({
        event: 'qs.data.file-unavailable',
        path,
        status: describeFileError(path, error).status,
        preview: this.context.preview(),
      }));
    } finally { this.loading.set(false); }
  }

  clearFile(): void { this.file.set(null); this.fileError.set(null); }

  async mutateThread(request: ThreadMutationRequest): Promise<ReviewThread> {
    const thread = await firstValueFrom(this.http.post<ReviewThread>(`${this.context.repositoryApiBase()}/threads`, request));
    await this.loadFile(request.path);
    console.info(JSON.stringify({ event: 'qs.thread.mutated', threadId: thread.id, path: request.path, status: thread.status, hasEntry: !!request.body }));
    return thread;
  }

  async mutateFindingState(request: FindingStateMutationRequest): Promise<ReviewFinding | null> {
    await firstValueFrom(this.http.post(`${this.context.repositoryApiBase()}/findings/state`, request));
    await Promise.all([this.loadFile(request.path), this.onFindingsChanged?.() ?? Promise.resolve()]);
    console.info(JSON.stringify({ event: 'qs.finding.state-mutated', fingerprint: request.fingerprint, path: request.path, state: request.state }));
    return this.file()?.metaDocuments.find(meta => meta.kind === request.kind)?.findings
      .find(finding => finding.fingerprint === request.fingerprint) ?? null;
  }

  createTask(request: HandoverRequest): Promise<HandoverResult> {
    return firstValueFrom(this.http.post<HandoverResult>(`${this.context.repositoryApiBase()}/handover`, request));
  }

  /**
   * The ignore list is repository-owned and revisioned. Only the API writes the file; the browser
   * sends the revision it last saw so a concurrent edit is rejected instead of silently overwritten.
   */
  async loadFindingSuppressions(): Promise<FindingSuppressionsResponse> {
    const response = await firstValueFrom(this.http.get<FindingSuppressionsResponse>(`${this.context.repositoryApiBase()}/findings/suppressions`));
    this.findingSuppressions.set(response);
    return response;
  }

  async addFindingSuppression(request: FindingSuppressionMutation): Promise<ReviewFinding | null> {
    const response = await firstValueFrom(this.http.post<FindingSuppressionsResponse>(
      `${this.context.repositoryApiBase()}/findings/suppressions`, request));
    this.findingSuppressions.set(response);
    await Promise.all([this.loadFile(request.path), this.onFindingsChanged?.() ?? Promise.resolve()]);
    console.info(JSON.stringify({ event: 'qs.finding.suppressed', fingerprint: request.fingerprint, revision: response.revision }));
    return this.file()?.metaDocuments.find(meta => meta.kind === request.kind)?.findings
      .find(finding => finding.fingerprint === request.fingerprint) ?? null;
  }

  async deleteFindingSuppression(id: string, expectedRevision: number): Promise<void> {
    const response = await firstValueFrom(this.http.delete<FindingSuppressionsResponse>(
      `${this.context.repositoryApiBase()}/findings/suppressions/${encodeURIComponent(id)}`,
      { params: { expectedRevision } }));
    this.findingSuppressions.set(response);
    const path = this.file()?.path;
    await Promise.all([path ? this.loadFile(path) : Promise.resolve(), this.onFindingsChanged?.() ?? Promise.resolve()]);
    console.info(JSON.stringify({ event: 'qs.finding.unsuppressed', suppressionId: id, revision: response.revision }));
  }

  async loadHandoverConfiguration(): Promise<void> {
    try {
      const configuration = await firstValueFrom(this.http.get<{ targetConfigured: boolean; dryRun: boolean }>(`${this.context.repositoryApiBase()}/handover`));
      this.handoverConfigured.set(configuration.targetConfigured);
      this.handoverDryRun.set(configuration.dryRun);
    } catch {
      this.handoverConfigured.set(false);
    }
  }

  /** Loads the demonstration fixtures on demand so they stay out of the production entry bundle. */
  async previewFixtures(): Promise<PreviewFixtures | null> {
    try {
      this.loadedPreviewFixtures ??= (await import('./preview-fixtures')).previewFixtures;
      return this.loadedPreviewFixtures;
    } catch {
      return null;
    }
  }

  /** Serves the labelled preview document; the editor pairs it with a banner and hides mutations. */
  private async showPreviewFile(path: string): Promise<void> {
    const fixtures = await this.previewFixtures();
    if (!fixtures) {
      this.file.set(null);
      this.fileError.set(describeFileError(path, null));
      return;
    }
    this.fileError.set(null);
    this.file.set({
      path,
      content: fixtures.content,
      metaDocuments: fixtures.metaDocuments,
      sizeBytes: fixtures.sizeBytes,
      lineEnding: 'lf',
      encoding: 'utf-8',
      coverage: unknownCoverage(),
    });
  }
}
