import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { describeFileError } from './api-errors';
import type { PreviewFixtures } from './preview-fixtures';

import {
  AgentStudioImportResponse,
  ApiConnectionState,
  AttackCoverageMatrix,
  CoverageFact,
  FileDocument,
  FileError,
  FindingStateMutationRequest,
  Guideline,
  GuidelineCatalogueEntry,
  GuidelineDraft,
  GuidelineImpact,
  GuidelineTrace,
  HandoverRequest,
  HandoverResult,
  KindState,
  ProjectDashboard,
  QualityRunReport,
  QualityRunTrendPage,
  QuotaReport,
  RepositoryRegistration,
  RepositoryRegistrationRequest,
  RepositoryTransition,
  ResolvedInputs,
  ReviewFinding,
  ReviewKind,
  ReviewMetaDocument,
  ReviewModelCatalog,
  ReviewModelRecommendation,
  ReviewPreflight,
  ReviewRun,
  ReviewRunCompareResult,
  ReviewRunRetention,
  ReviewState,
  ReviewThread,
  RiskReport,
  RunReportFormat,
  ScanReport,
  ScopeRuleMutation,
  ScopeRuleView,
  ScopeRulesResponse,
  SecurityScanResponse,
  StartReviewRequest,
  ThreadMutationRequest,
  TreeNode,
  UsageReport,
} from './contracts';

const emptyUsageReport = (): UsageReport => ({ generatedAt: '', runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 0, byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [] });
const unknownCoverage = (): CoverageFact => ({ state: 'unknown', coveredLines: 0, totalLines: 0, coveredBranches: 0, totalBranches: 0, linePercent: null, branchPercent: null, commit: null, measuredAt: null, filesWithData: 0 });


@Injectable({ providedIn: 'root' })
export class QualityApi {
  private readonly http = inject(HttpClient);
  private legacyApi = false;
  readonly tree = signal<TreeNode[]>([]);
  readonly file = signal<FileDocument | null>(null);
  readonly fileError = signal<FileError | null>(null);
  readonly scan = signal<ScanReport>({ files: [], freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0 });
  readonly security = signal<SecurityScanResponse | null>(null);
  readonly attackCoverage = signal<AttackCoverageMatrix | null>(null);
  readonly attackCoverageLoading = signal(false);
  readonly attackCoverageError = signal('');
  readonly risk = signal<RiskReport>({ days: 90, currentCommit: null, rows: [], matrix: [] });
  readonly project = signal<ProjectDashboard | null>(null);
  readonly projectLoading = signal(true);
  readonly projectError = signal('');
  readonly repositoryTransition = signal<RepositoryTransition | null>(null);
  readonly connectionState = signal<ApiConnectionState>('connecting');
  readonly connectionError = signal('');
  readonly connected = computed(() => this.connectionState() === 'live');
  /** True while the shell shows labelled demonstration data because the API is unreachable. */
  readonly preview = computed(() => this.connectionState() === 'preview');
  readonly connectionLabel = computed(() => {
    const state = this.connectionState();
    return state === 'live'
      ? 'Repository connected'
      : state === 'preview'
        ? 'API offline, preview data'
        : state === 'offline'
          ? 'API offline'
          : 'Connecting to API';
  });
  readonly loading = signal(false);
  readonly handoverConfigured = signal(false);
  readonly handoverDryRun = signal(true);
  readonly inputs = signal<Partial<Record<ReviewKind, ResolvedInputs>>>({});
  readonly guidelines = signal<Guideline[]>([]);
  readonly guidelineCatalogue = signal<GuidelineCatalogueEntry[]>([]);
  readonly guidelineTraces = signal<GuidelineTrace[]>([]);
  readonly repositories = signal<RepositoryRegistration[]>([]);
  readonly selectedRepositoryId = signal('default');
  readonly selectedRepository = computed(() => this.repositories().find(repository => repository.id === this.selectedRepositoryId()) ?? null);
  readonly modelCatalog = signal<ReviewModelCatalog>({ schemaVersion: 1, policyVersion: '', evidenceAsOfDate: '', sourceRepository: 'agent-orc/token-economy', sourceCommit: '', thinkingLevels: [], models: [] });
  readonly reviewRuns = signal<ReviewRun[]>([]);
  readonly scopeRules = signal<ScopeRulesResponse>({ schema: '', rules: [] });
  readonly usage = signal<UsageReport>(emptyUsageReport());
  readonly quotas = signal<QuotaReport>({ at: '', ttlSeconds: 0, providers: [] });
  readonly reviewError = signal('');
  readonly focusedThreadId = signal<string | null>(null);
  private readonly treeSnapshots = new Map<string, [TreeNode[], string | null]>();
  private readonly projectSnapshots = new Map<string, [ProjectDashboard, string | null]>();
  private repositorySelectionSequence = 0;
  private reviewPollTimer: ReturnType<typeof setTimeout> | null = null;
  private loadedPreviewFixtures: PreviewFixtures | null = null;

  async loadRepositories(preferredId?: string | null): Promise<void> {
    try {
      const result = await firstValueFrom(this.http.get<{ repositories: RepositoryRegistration[]; defaultRepositoryId: string }>('/api/repos'));
      this.legacyApi = false;
      this.repositories.set(result.repositories);
      const selected = result.repositories.some(repository => repository.id === preferredId)
        ? preferredId!
        : result.repositories.some(repository => repository.id === this.selectedRepositoryId())
          ? this.selectedRepositoryId()
          : result.defaultRepositoryId;
      this.selectedRepositoryId.set(selected);
    } catch (error) {
      // A pre-registry server still exposes the legacy default endpoints.
      this.legacyApi = true;
      this.repositories.set([{ id: 'default', displayName: 'Default repository', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null }]);
      this.selectedRepositoryId.set('default');
      console.warn(JSON.stringify({ event: 'qs.repositories.legacy-fallback', reason: this.errorMessage(error) }));
    }
  }

  async selectRepository(id: string): Promise<void> {
    const started = performance.now();
    const sequence = ++this.repositorySelectionSequence;
    this.selectedRepositoryId.set(id);
    this.connectionState.set('connecting');
    this.file.set(null);
    this.attackCoverage.set(null);
    const treeSnapshot = this.treeSnapshots.get(`${id}\0`)?.[0];
    const projectSnapshot = this.projectSnapshots.get(id)?.[0];
    this.tree.set(treeSnapshot ?? []);
    this.project.set(projectSnapshot ?? null);
    this.repositoryTransition.set({ repositoryId: id, hasSnapshot: projectSnapshot !== undefined });
    this.usage.set(emptyUsageReport());
    await Promise.all([this.loadProjectDashboard(id), this.loadTree(id, false)]);
    if (sequence !== this.repositorySelectionSequence) return;
    const detailsLoading = Promise.all([
      this.loadRepositoryDetails(id),
      this.loadReviewRuns(id),
      this.loadUsage(undefined, undefined, id),
    ]);
    void detailsLoading.finally(() => {
      const remaining = Math.max(0, 250 - (performance.now() - started));
      setTimeout(() => {
        if (sequence === this.repositorySelectionSequence) this.repositoryTransition.set(null);
      }, remaining);
    });
    console.info(JSON.stringify({ event: 'qs.repository.selected', repositoryId: id }));
  }

  async createRepository(request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> {
    const created = await firstValueFrom(this.http.post<RepositoryRegistration>('/api/repos', request));
    await this.loadRepositories(created.id);
    return created;
  }

  async updateRepository(id: string, request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> {
    const updated = await firstValueFrom(this.http.put<RepositoryRegistration>(`/api/repos/${encodeURIComponent(id)}`, request));
    await this.loadRepositories(id);
    return updated;
  }

  async archiveRepository(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/repos/${encodeURIComponent(id)}`));
    await this.loadRepositories(id === this.selectedRepositoryId() ? null : this.selectedRepositoryId());
  }

  async importFromAgentStudio(): Promise<AgentStudioImportResponse> {
    const result = await firstValueFrom(this.http.post<AgentStudioImportResponse>('/api/repos/import-from-agent-studio', {}));
    console.info(JSON.stringify({ event: 'qs.repositories.agent-studio-import', imported: result.imported, skipped: result.skipped, failed: result.failed }));
    await this.loadRepositories(this.selectedRepositoryId());
    return result;
  }

  async loadTree(repositoryId = this.selectedRepositoryId(), waitForDetails = true, path = ''): Promise<void> {
    const base = this.repositoryApiBase(repositoryId);
    const snapshotKey = `${repositoryId}\0${path}`;
    const retained = this.treeSnapshots.get(snapshotKey);
    const detailsLoading = waitForDetails ? this.loadRepositoryDetails(repositoryId) : null;
    try {
      const response = await firstValueFrom(this.http.get<{ nodes: TreeNode[] }>(
        `${base}/tree?path=${encodeURIComponent(path)}`,
        { observe: 'response', headers: retained?.[1] ? { 'If-None-Match': retained[1] } : undefined }));
      const nodes = response.body!.nodes;
      this.treeSnapshots.set(snapshotKey, [nodes, response.headers.get('ETag')]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.tree.set(nodes); this.connectionState.set('live'); this.connectionError.set('');
      console.info(JSON.stringify({ event: 'qs.data.tree-loaded', nodeCount: nodes.length, source: 'api' }));
    } catch (error) {
      if (!this.reuseSnapshot(error, repositoryId, retained) && repositoryId === this.selectedRepositoryId()) {
        // Only a request that never reached the API earns preview data; a reachable API that
        // answered with an error keeps the tree empty and states the reason.
        if (this.unreachable(error)) {
          this.connectionState.set('preview');
          const fixtures = await this.previewFixtures();
          if (fixtures && repositoryId === this.selectedRepositoryId()) this.tree.set(fixtures.tree);
        } else {
          this.connectionState.set('offline');
        }
        this.connectionError.set(this.errorMessage(error));
        console.warn(JSON.stringify({
          event: 'qs.data.tree-unavailable',
          repositoryId,
          preview: this.connectionState() === 'preview',
          reason: this.errorMessage(error),
        }));
      }
    }
    if (detailsLoading) await detailsLoading;
  }

  async loadAttackCoverage(scope = 'src/QualityStudio.Api'): Promise<AttackCoverageMatrix> {
    this.attackCoverageLoading.set(true);
    this.attackCoverageError.set('');
    try {
      const matrix = await firstValueFrom(this.http.get<AttackCoverageMatrix>(
        `${this.repositoryApiBase()}/security/attack-coverage`, { params: { path: scope } }));
      this.attackCoverage.set(matrix);
      console.info(JSON.stringify({ event: 'qs.security.attack-coverage-loaded', scope, cells: matrix.cellCount, stale: matrix.staleCount, deferred: matrix.notYetCheckedCount, disagreements: matrix.disagreementCount }));
      return matrix;
    } catch (error) {
      this.attackCoverageError.set(this.errorMessage(error));
      throw error;
    } finally {
      this.attackCoverageLoading.set(false);
    }
  }

  async startReview(request: StartReviewRequest): Promise<ReviewRun> {
    this.reviewError.set('');
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review`, request));
      this.reviewRuns.update(runs => [run, ...runs.filter(candidate => candidate.id !== run.id)]);
      this.scheduleReviewPoll();
      console.info(JSON.stringify({ event: 'qs.review.queued', runId: run.id, path: run.path, kind: run.kind, fileCount: run.totalFiles }));
      return run;
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
      throw error;
    }
  }

  async loadModelCatalog(): Promise<void> {
    try {
      this.modelCatalog.set(await firstValueFrom(this.http.get<ReviewModelCatalog>('/api/models')));
    } catch (error) {
      console.warn(JSON.stringify({ event: 'qs.models.unavailable', reason: this.errorMessage(error) }));
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

  async estimateReview(request: StartReviewRequest): Promise<ReviewPreflight> {
    this.reviewError.set('');
    try {
      return await firstValueFrom(this.http.post<ReviewPreflight>(`${this.repositoryApiBase()}/review/estimate`, request));
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
      throw error;
    }
  }

  async loadReviewRuns(repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (!this.connected() || repositoryId !== this.selectedRepositoryId()) return;
    try {
      const before = new Map(this.reviewRuns().map(run => [run.id, run.state]));
      const result = await firstValueFrom(this.http.get<{ runs: ReviewRun[] }>(`${this.repositoryApiBase(repositoryId)}/review/runs`));
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.reviewRuns.set(result.runs);
      const completed = result.runs.some(run => ['done', 'failed', 'cancelled', 'capped'].includes(run.state) && ['queued', 'running'].includes(before.get(run.id) ?? ''));
      if (completed) {
        const openPath = this.file()?.path;
        await this.loadTree();
        void this.loadProjectDashboard();
        if (openPath) await this.loadFile(openPath);
        await Promise.all([this.loadUsage(), this.loadQuotas()]);
      }
      if (result.runs.some(run => run.state === 'queued' || run.state === 'running')) this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async loadRunReport(id: string): Promise<QualityRunReport> {
    return await firstValueFrom(this.http.get<QualityRunReport>(
      `${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/report`,
      { params: { format: 'json' } }));
  }

  async loadRunTrend(kind: ReviewKind, scopeUnitId: string, level: string, cursor?: string): Promise<QualityRunTrendPage> {
    const params: Record<string, string> = { kind, scopeUnitId, level, limit: '30' };
    if (cursor) params['cursor'] = cursor;
    return await firstValueFrom(this.http.get<QualityRunTrendPage>(
      `${this.repositoryApiBase()}/review/runs/trend`, { params }));
  }

  async compareRuns(baselineId: string, candidateId: string): Promise<ReviewRunCompareResult> {
    return await firstValueFrom(this.http.get<ReviewRunCompareResult>(
      `${this.repositoryApiBase()}/review/runs/compare`, { params: { baselineId, candidateId } }));
  }

  async loadRunRetention(): Promise<ReviewRunRetention> {
    return await firstValueFrom(this.http.get<ReviewRunRetention>(`${this.repositoryApiBase()}/review/runs/retention`));
  }

  async loadPinnedRunIds(): Promise<string[]> {
    const result = await firstValueFrom(this.http.get<{ pinnedRunIds: string[] }>(`${this.repositoryApiBase()}/review/runs/pins`));
    return result.pinnedRunIds;
  }

  async pinRun(id: string): Promise<string[]> {
    const result = await firstValueFrom(this.http.post<{ pinnedRunIds: string[] }>(
      `${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pin`, {}));
    return result.pinnedRunIds;
  }

  async unpinRun(id: string): Promise<string[]> {
    const result = await firstValueFrom(this.http.delete<{ pinnedRunIds: string[] }>(
      `${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pin`));
    return result.pinnedRunIds;
  }

  runReportUrl(id: string, format: RunReportFormat): string {
    return `${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/report?format=${format}`;
  }

  runReportFileName(id: string, format: RunReportFormat): string {
    const extension = format === 'markdown' ? 'md' : format === 'sarif' ? 'sarif' : format;
    return `quality-run-${id}.${extension}`;
  }

  repositoryReportUrl(format: RunReportFormat = 'html'): string {
    return `${this.repositoryApiBase()}/report?format=${format}`;
  }

  async loadUsage(since?: string, kind?: ReviewKind, repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (!this.connected() || repositoryId !== this.selectedRepositoryId()) return;
    try {
      const params: Record<string, string> = {};
      if (since) params['since'] = since;
      if (kind) params['kind'] = kind;
      const usage = await firstValueFrom(this.http.get<UsageReport>(`${this.repositoryApiBase(repositoryId)}/usage`, { params }));
      if (repositoryId === this.selectedRepositoryId()) this.usage.set(usage);
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        this.usage.set(emptyUsageReport());
        console.warn(JSON.stringify({ event: 'qs.usage.unavailable', reason: this.errorMessage(error) }));
      }
    }
  }

  async loadQuotas(): Promise<void> {
    try {
      this.quotas.set(await firstValueFrom(this.http.get<QuotaReport>('/api/quotas')));
    } catch (error) {
      this.quotas.set({ at: new Date().toISOString(), ttlSeconds: 0, providers: [] });
      console.warn(JSON.stringify({ event: 'qs.quotas.unavailable', reason: this.errorMessage(error) }));
    }
  }

  async cancelReview(id: string): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.delete<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}`));
      this.reviewRuns.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
      const openPath = this.file()?.path;
      await this.loadTree();
      void this.loadProjectDashboard();
      if (openPath) await this.loadFile(openPath);
      this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async pauseReview(id: string): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/pause`, {}));
      this.reviewRuns.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async resumeReview(id: string, cap: { tokenCap?: number | null; costCap?: number | null } = {}): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/resume`, cap));
      this.reviewRuns.update(runs => runs.map(candidate => candidate.id === run.id ? run : candidate));
      this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  private scheduleReviewPoll(): void {
    if (this.reviewPollTimer !== null) return;
    this.reviewPollTimer = setTimeout(() => {
      this.reviewPollTimer = null;
      void this.loadReviewRuns();
    }, 1500);
  }

  async loadFile(path: string): Promise<void> {
    this.loading.set(true);
    try {
      const file = await firstValueFrom(this.http.get<FileDocument>(`${this.repositoryApiBase()}/file`, { params: { path } }));
      this.file.set(file);
      this.fileError.set(null);
      this.connectionState.set('live');
    } catch (error) {
      // A failed lookup never becomes someone else's source. The editor renders the reason.
      // Preview fixtures are reserved for the genuinely unreachable API and carry their own banner.
      if (this.unreachable(error)) {
        this.connectionState.set('preview');
        await this.showPreviewFile(path);
      } else {
        this.file.set(null);
        this.fileError.set(describeFileError(path, error));
        if (this.connectionState() === 'connecting') this.connectionState.set('offline');
      }
      console.warn(JSON.stringify({
        event: 'qs.data.file-unavailable',
        path,
        status: error instanceof HttpErrorResponse ? error.status : null,
        preview: this.connectionState() === 'preview',
      }));
    } finally { this.loading.set(false); }
  }

  clearFile(): void { this.file.set(null); this.fileError.set(null); }

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

  /** Loads the demonstration fixtures on demand so they stay out of the production entry bundle. */
  private async previewFixtures(): Promise<PreviewFixtures | null> {
    try {
      this.loadedPreviewFixtures ??= (await import('./preview-fixtures')).previewFixtures;
      return this.loadedPreviewFixtures;
    } catch {
      return null;
    }
  }

  /** True when the request never reached the API, so no server-side judgement exists. */
  private unreachable(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 0;
  }

  async loadProjectDashboard(repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (repositoryId === this.selectedRepositoryId()) {
      this.projectLoading.set(true);
      this.projectError.set('');
    }
    const start = performance.now();
    const retained = this.projectSnapshots.get(repositoryId);
    try {
      const response = await firstValueFrom(this.http.get<ProjectDashboard>(
        `${this.repositoryApiBase(repositoryId)}/project`,
        { observe: 'response', headers: retained?.[1] ? { 'If-None-Match': retained[1] } : undefined }));
      const project = response.body!;
      this.projectSnapshots.set(repositoryId, [project, response.headers.get('ETag')]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.project.set(project);
      requestAnimationFrame(() => {
        if (repositoryId !== this.selectedRepositoryId()) return;
        const duration = performance.now() - start;
        performance.measure('qs.project.first-interactive', { start, end: performance.now(), detail: { budget: 150, repositoryId } });
        console.info(JSON.stringify({ event: 'qs.project.first-interactive', repositoryId, durationMs: +duration.toFixed(2), budgetMs: 150, withinBudget: duration < 150 }));
      });
    } catch (error) {
      if (!this.reuseSnapshot(error, repositoryId, retained) && repositoryId === this.selectedRepositoryId()) {
        if (!retained) this.project.set(null);
        this.projectError.set(this.errorMessage(error));
        console.warn(JSON.stringify({ event: 'qs.project.unavailable', repositoryId, reason: this.errorMessage(error) }));
      }
    } finally {
      if (repositoryId === this.selectedRepositoryId()) this.projectLoading.set(false);
    }
  }

  async createTask(request: HandoverRequest): Promise<HandoverResult> {
    return firstValueFrom(this.http.post<HandoverResult>(`${this.repositoryApiBase()}/handover`, request));
  }

  async mutateThread(request: ThreadMutationRequest): Promise<ReviewThread> {
    const thread = await firstValueFrom(this.http.post<ReviewThread>(`${this.repositoryApiBase()}/threads`, request));
    await this.loadFile(request.path);
    console.info(JSON.stringify({ event: 'qs.thread.mutated', threadId: thread.id, path: request.path, status: thread.status, hasEntry: !!request.body }));
    return thread;
  }

  async mutateFindingState(request: FindingStateMutationRequest): Promise<ReviewFinding | null> {
    await firstValueFrom(this.http.post(`${this.repositoryApiBase()}/findings/state`, request));
    await Promise.all([this.loadFile(request.path), this.loadTree()]);
    console.info(JSON.stringify({ event: 'qs.finding.state-mutated', fingerprint: request.fingerprint, path: request.path, state: request.state }));
    return this.file()?.metaDocuments.find(meta => meta.kind === request.kind)?.findings
      .find(finding => finding.fingerprint === request.fingerprint) ?? null;
  }

  async loadScopeRules(): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.get<ScopeRulesResponse>(`${this.repositoryApiBase()}/scope/rules`));
    this.scopeRules.set(response);
    return response;
  }

  previewScopeRule(request: ScopeRuleMutation): Promise<ScopeRuleView> {
    return firstValueFrom(this.http.post<ScopeRuleView>(`${this.repositoryApiBase()}/scope/rules/preview`, request));
  }

  async addScopeRule(request: ScopeRuleMutation): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.post<ScopeRulesResponse>(`${this.repositoryApiBase()}/scope/rules`, request));
    this.scopeRules.set(response);
    await this.loadTree();
    return response;
  }

  async updateScopeRule(index: number, request: ScopeRuleMutation): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.put<ScopeRulesResponse>(
      `${this.repositoryApiBase()}/scope/rules/${index}`, request));
    this.scopeRules.set(response);
    await this.loadTree();
    return response;
  }

  async deleteScopeRule(index: number): Promise<ScopeRulesResponse> {
    const response = await firstValueFrom(this.http.delete<ScopeRulesResponse>(`${this.repositoryApiBase()}/scope/rules/${index}`));
    this.scopeRules.set(response);
    await this.loadTree();
    return response;
  }

  async createGuideline(draft: GuidelineDraft): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.post<Guideline>(`${this.repositoryApiBase()}/guidelines`, draft));
    await this.loadTree();
    return guideline;
  }

  async updateGuideline(existingId: string, draft: GuidelineDraft): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.put<Guideline>(`${this.repositoryApiBase()}/guidelines/${encodeURIComponent(existingId)}`, draft));
    await this.loadTree();
    return guideline;
  }

  async deleteGuideline(id: string): Promise<void> {
    await firstValueFrom(this.http.delete(`${this.repositoryApiBase()}/guidelines/${encodeURIComponent(id)}`));
    await this.loadTree();
  }

  async installGuideline(catalogueId: string): Promise<Guideline> {
    const guideline = await firstValueFrom(this.http.post<Guideline>(`${this.repositoryApiBase()}/guidelines/catalog/${encodeURIComponent(catalogueId)}/install`, {}));
    await this.loadTree();
    return guideline;
  }

  async guidelineImpact(guideline: GuidelineDraft, samplePaths: string[], kind: ReviewKind): Promise<GuidelineImpact> {
    return firstValueFrom(this.http.post<GuidelineImpact>(`${this.repositoryApiBase()}/guidelines/impact`, { guideline, samplePaths, kind }));
  }

  private async loadRepositoryDetails(repositoryId: string): Promise<void> {
    const base = this.repositoryApiBase(repositoryId);
    try {
      const [scan, inputs, guidelines, risk] = await Promise.all([
        firstValueFrom(this.http.get<ScanReport>(`${base}/scan`)),
        firstValueFrom(this.http.get<{ kinds: Record<ReviewKind, ResolvedInputs> }>(`${base}/inputs`)),
        firstValueFrom(this.http.get<{ guidelines: Guideline[]; catalogue: GuidelineCatalogueEntry[]; traces: GuidelineTrace[] }>(`${base}/guidelines`)),
        firstValueFrom(this.http.get<RiskReport>(`${base}/risk?days=90`)),
      ]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.scan.set(scan); this.inputs.set(inputs.kinds);
      this.guidelines.set(guidelines.guidelines); this.guidelineCatalogue.set(guidelines.catalogue); this.guidelineTraces.set(guidelines.traces);
      this.risk.set(risk);
      await this.loadHandoverConfiguration();
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        console.warn(JSON.stringify({ event: 'qs.repository.details-unavailable', repositoryId, reason: this.errorMessage(error) }));
      }
    }
  }

  private async loadHandoverConfiguration(): Promise<void> {
    try {
      const configuration = await firstValueFrom(this.http.get<{ targetConfigured: boolean; dryRun: boolean }>(`${this.repositoryApiBase()}/handover`));
      this.handoverConfigured.set(configuration.targetConfigured);
      this.handoverDryRun.set(configuration.dryRun);
    } catch {
      this.handoverConfigured.set(false);
    }
  }

  errorMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      return error.error?.detail || error.error?.title || error.message;
    }
    return error instanceof Error ? error.message : 'The repository request failed.';
  }

  private repositoryApiBase(repositoryId = this.selectedRepositoryId()): string {
    return this.legacyApi ? '/api' : `/api/repos/${encodeURIComponent(repositoryId)}`;
  }

  private reuseSnapshot(error: unknown, repositoryId: string, retained: unknown): boolean {
    if (!retained || !(error instanceof HttpErrorResponse) || error.status !== 304) return false;
    if (repositoryId === this.selectedRepositoryId()) {
      this.connectionState.set('live');
      if (this.repositoryTransition()?.repositoryId === repositoryId) this.repositoryTransition.set(null);
    }
    return true;
  }
}
