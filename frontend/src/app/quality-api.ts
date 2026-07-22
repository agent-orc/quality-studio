import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ReviewState = 'fresh' | 'stale' | 'missing';
export interface KindState { direct: ReviewState; descendants: ReviewState; overall: ReviewState; score: number | null; band: string | null; metaPath: string | null; }
export interface TreeNode {
  id: string;
  name: string;
  level: string;
  path: string;
  kinds: Record<string, KindState>;
  findingsCount?: number;
  findingCounts?: FindingStateCounts;
  reviewedAt?: string | null;
  sizeBytes?: number | null;
  lineCount?: number | null;
  children: TreeNode[];
}
export type ReviewKind = 'code' | 'security' | 'performance';
export type FindingSeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';
export type FindingState = 'open' | 'accepted' | 'waived' | 'false-positive' | 'resolved';
export interface FindingStateCounts { open: number; accepted: number; waived: number; falsePositive: number; resolved: number; }
export interface FindingPosition { line: number; column: number; }
export interface FindingLocation { path: string; range?: { start: FindingPosition; end: FindingPosition }; }
export interface ReviewFinding { id: string; aspect: string; severity: FindingSeverity; title: string; description: string; recommendation: string; evidence?: string; fingerprint?: string; ruleId?: string; accepted?: boolean; state?: FindingState; stateAuthor?: string; stateReason?: string; stateTimestamp?: string; stateExpiresAt?: string; locations: FindingLocation[]; }
export type ThreadStatus = 'open' | 'resolved';
export type AnchorState = 'anchored' | 'healed' | 'detached';
export interface ReviewThreadAuthor { kind: 'agent' | 'human'; agent?: string; model?: string; name?: string; }
export interface ReviewThreadEntry { id: string; author: ReviewThreadAuthor; createdAt: string; body: string; replyTo?: string; }
export interface ReviewThread { id: string; anchor: { path: string; fingerprint: string; contextHash: string; lastKnownRange: { start: FindingPosition; end: FindingPosition } }; status: ThreadStatus; anchorState?: AnchorState; healedAt?: string; entries: ReviewThreadEntry[]; }
export interface TokenUsage { inputTokens: number | null; outputTokens: number | null; cachedInputTokens: number | null; reasoningOutputTokens: number | null; durationMs: number; }
export interface ReviewMetaDocument { reviewedAt: string; kind: ReviewKind; reviewer: { agent: string; model: string; runId?: string; usage?: TokenUsage & { cliType: string } }; grade: { score: number; band: string; rationale: string }; summary: string; findings: ReviewFinding[]; findingCounts?: FindingStateCounts; threads?: ReviewThread[]; }
export interface ThreadMutationRequest { path: string; kind: ReviewKind; threadId?: string; body?: string; replyTo?: string; status?: ThreadStatus; humanName?: string; line?: number; findingFingerprint?: string; }
export interface FindingStateMutationRequest { path: string; kind: ReviewKind; fingerprint: string; state: Exclude<FindingState, 'resolved'>; author: string; reason: string; expiresAt?: string | null; expectedTimestamp?: string | null; }
export type SecurityVerdict = 'pass' | 'warn' | 'block' | 'unavailable';
export interface SecurityScanProvenance { scanner: string; version: string; mode: string; range: string | null; configPath: string | null; baselinePath: string | null; scannedAt: string; }
export interface SecurityScanCounts { filesScanned: number; newFindings: number; acceptedFindings: number; blockFindings: number; warnFindings: number; cleanFiles: number; }
export interface SecurityScanFinding extends ReviewFinding { path: string; }
export interface SecurityScanResponse {
  verdict: SecurityVerdict;
  available: boolean;
  scanner: string;
  version: string;
  mode: string;
  range: string | null;
  configPath: string | null;
  baselinePath: string | null;
  scannedAt: string;
  filesScanned: number;
  newFindings: number;
  acceptedFindings: number;
  blockFindings: number;
  warnFindings: number;
  cleanFiles: number;
  unavailableReason: string | null;
  provenance: SecurityScanProvenance;
  counts: SecurityScanCounts;
  findings: SecurityScanFinding[];
}
export type LineEnding = 'lf' | 'crlf' | 'mixed';
export type FileEncoding = 'utf-8' | 'utf-8-bom' | 'other';
export interface FileDocument { path: string; content: string; metaDocuments: ReviewMetaDocument[]; sizeBytes: number; lineEnding: LineEnding; encoding: FileEncoding; }
export interface ScanReport { files: unknown[]; freshCount: number; staleCount: number; missingCount: number; }
export interface HandoverRequest { findingSummary: string; filePath: string; findingText: string; reviewKind: string; metaReference: string; }
export interface HandoverResult { dryRun: boolean; taskId: string | null; card: { title: string }; }
export interface ResolvedInput { id: string; source: string; scope: 'global' | 'project'; priority: number; includedContent: string; content: string; truncated: boolean; }
export interface InputOmission { id: string; source: string; reason: string; omittedCharacters: number; }
export interface ResolvedInputs { kind: ReviewKind; level: string; budgetCharacters: number; includedCharacters: number; complete: boolean; inputs: ResolvedInput[]; omissions: InputOmission[]; }
export type ApiConnectionState = 'connecting' | 'live' | 'preview' | 'offline';
export interface RepositoryRegistration {
  id: string;
  displayName: string;
  rootPath: string;
  globalInputsDirectory: string | null;
  inputBudgetCharacters: number;
  enabledReviewKinds: ReviewKind[];
  archived: boolean;
}
export interface RepositoryRegistrationRequest {
  id?: string;
  displayName: string;
  rootPath: string;
  globalInputsDirectory: string | null;
  inputBudgetCharacters: number;
  enabledReviewKinds: ReviewKind[];
}
export type AgentStudioImportStatus = 'imported' | 'skipped' | 'failed';
export interface AgentStudioImportResult {
  projectId: string;
  displayName: string;
  repositoryPath: string | null;
  status: AgentStudioImportStatus;
  repositoryId: string | null;
  reason: string | null;
}
export interface AgentStudioImportResponse { results: AgentStudioImportResult[]; imported: number; skipped: number; failed: number; }
export type ReviewRunState = 'queued' | 'running' | 'paused' | 'done' | 'failed' | 'cancelled';
export interface ReviewFileProgress { path: string; state: ReviewRunState; startedAt: string | null; finishedAt: string | null; error: string | null; }
export interface ReviewRun {
  id: string; repositoryId: string; path: string; level: string; kind: ReviewKind; model: string | null; cliType: string;
  state: ReviewRunState; totalFiles: number; completedFiles: number; failedFiles: number; createdAt: string;
  startedAt: string | null; finishedAt: string | null; files: ReviewFileProgress[]; errors: string[]; usageOperations: number; usage: TokenUsage;
}
export interface StartReviewRequest { path: string; kind: ReviewKind; model?: string | null; cliType?: string | null; }
export interface UsageAggregate { key: string; runs: number; inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; durationMs: number; }
export interface UsageEntry { runId: string; timestamp: string; model: string; cliType: string; tokens: TokenUsage; kind: ReviewKind; level: string; path: string; }
export interface UsageReport { generatedAt: string; runs: number; inputTokens: number; outputTokens: number; cachedInputTokens: number; reasoningOutputTokens: number; durationMs: number; byModel: UsageAggregate[]; byKind: UsageAggregate[]; byDay: UsageAggregate[]; recent: UsageEntry[]; }
export interface QuotaWindow { label: string; usedPct: number | null; remainingPct: number | null; used: number | null; limit: number | null; unit: string | null; resetAt: string | null; resetLabel: string | null; }
export interface QuotaProvider { provider: string; plan: string | null; fetchedAt: string; source: string | null; error: string | null; windows: QuotaWindow[]; }
export interface QuotaReport { at: string; ttlSeconds: number; providers: QuotaProvider[]; }

const emptyUsageReport = (): UsageReport => ({ generatedAt: '', runs: 0, inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 0, byModel: [], byKind: [], byDay: [], recent: [] });

const demoFile = `using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<RepositoryAccess>();

var app = builder.Build();
app.UseExceptionHandler();

app.MapGet("/api/tree", (RepositoryAccess repository) =>
{
    var stopwatch = Stopwatch.StartNew();
    var projects = RepositoryHierarchyBuilder.BuildDotNet(repository.Root);
    return Results.Ok(projects);
});

app.MapGet("/api/file", async (string path) =>
{
    var content = await File.ReadAllTextAsync(path);
    return Results.Ok(content);
});

app.Run();`;
const demoFileSizeBytes = new TextEncoder().encode(demoFile).length;

const state = (overall: ReviewState, score: number | null, band: string | null): KindState => ({ direct: overall, descendants: overall, overall, score, band, metaPath: score === null ? null : 'preview.review-meta.json' });
const kind = (code: ReviewState): Record<string, KindState> => ({
  code: state(code, code === 'fresh' ? 91 : code === 'stale' ? 72 : null, code === 'fresh' ? 'A' : code === 'stale' ? 'C' : null),
  security: state(code === 'fresh' ? 'fresh' : 'missing', code === 'fresh' ? 86 : null, code === 'fresh' ? 'B' : null),
  performance: state(code === 'missing' ? 'missing' : 'stale', code === 'missing' ? null : 72, code === 'missing' ? null : 'C'),
});
const demoTree: TreeNode[] = [{ id: 'quality-studio', name: 'Quality Studio', level: 'repository', path: '.', kinds: kind('stale'), children: [
  { id: 'src', name: 'src', level: 'folder', path: 'src', kinds: kind('stale'), children: [
    { id: 'api', name: 'QualityStudio.Api', level: 'project', path: 'src/QualityStudio.Api', kinds: kind('fresh'), children: [
      { id: 'program', name: 'Program.cs', level: 'file', path: 'src/QualityStudio.Api/Program.cs', kinds: kind('fresh'), children: [] },
      { id: 'contracts', name: 'ApiContracts.cs', level: 'file', path: 'src/QualityStudio.Api/ApiContracts.cs', kinds: kind('stale'), children: [] },
      { id: 'settings', name: 'appsettings.json', level: 'file', path: 'src/QualityStudio.Api/appsettings.json', kinds: kind('missing'), children: [] },
    ]},
    { id: 'core', name: 'AgentOrchestrator.CodeQuality', level: 'project', path: 'src/AgentOrchestrator.CodeQuality', kinds: kind('stale'), children: [
      { id: 'runner', name: 'ReviewRunner.cs', level: 'file', path: 'src/AgentOrchestrator.CodeQuality/ReviewRunner.cs', kinds: kind('stale'), children: [] },
      { id: 'state', name: 'ReviewState.cs', level: 'file', path: 'src/AgentOrchestrator.CodeQuality/ReviewState.cs', kinds: kind('fresh'), children: [] },
    ]},
  ]},
  { id: 'tests', name: 'tests', level: 'folder', path: 'tests', kinds: kind('missing'), children: [] },
  { id: 'docs', name: 'docs', level: 'folder', path: 'docs', kinds: kind('fresh'), children: [] },
]}];

const demoMeta: ReviewMetaDocument[] = [
  { reviewedAt: '2026-07-11T16:20:00.000Z', kind: 'code', reviewer: { agent: 'quality-reviewer', model: 'gpt-5' }, grade: { score: 91, band: 'A', rationale: 'Clear request boundaries and consistent error handling.' }, summary: 'The API entry point is compact and readable. One low-risk diagnostic gap remains.', findings: [{ id: 'route-timing', aspect: 'observability', severity: 'low', title: 'File route has no timing event', description: 'The user-visible file read is not timed, making slow repository access difficult to diagnose.', recommendation: 'Record a structured duration for the file-read path.', evidence: 'The route awaits File.ReadAllTextAsync and returns without a timing log.', locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 17, column: 1 }, end: { line: 21, column: 3 } } }] }] },
  { reviewedAt: '2026-07-09T10:05:00.000Z', kind: 'performance', reviewer: { agent: 'perf-reviewer', model: 'gpt-5' }, grade: { score: 72, band: 'C', rationale: 'Repository hierarchy work is repeated on the request path.' }, summary: 'The endpoint is correct, but the stored review predates the current file and should be rerun.', findings: [{ id: 'rebuild-tree', aspect: 'request-path', severity: 'high', title: 'Hierarchy rebuilt for every request', description: 'A full project hierarchy build runs synchronously whenever the tree endpoint is requested.', recommendation: 'Cache the derived hierarchy and invalidate it from repository scan events.', locations: [{ path: 'src/QualityStudio.Api/Program.cs', range: { start: { line: 10, column: 1 }, end: { line: 15, column: 3 } } }] }] },
  { reviewedAt: '2026-07-10T13:40:00.000Z', kind: 'security', reviewer: { agent: 'gitleaks', model: '8.24.2' }, grade: { score: 86, band: 'B', rationale: 'Repository access is constrained by the API service.' }, summary: 'No exploitable issue was identified in this file.', findings: [] },
];

const demoSecurity: SecurityScanResponse = {
  verdict: 'pass',
  available: true,
  scanner: 'gitleaks',
  version: '8.24.2',
  mode: 'repository',
  range: null,
  configPath: null,
  baselinePath: null,
  scannedAt: '2026-07-11T16:20:00.000Z',
  filesScanned: 1,
  newFindings: 0,
  acceptedFindings: 0,
  blockFindings: 0,
  warnFindings: 0,
  cleanFiles: 1,
  unavailableReason: null,
  provenance: { scanner: 'gitleaks', version: '8.24.2', mode: 'repository', range: null, configPath: null, baselinePath: null, scannedAt: '2026-07-11T16:20:00.000Z' },
  counts: { filesScanned: 1, newFindings: 0, acceptedFindings: 0, blockFindings: 0, warnFindings: 0, cleanFiles: 1 },
  findings: [],
};

@Injectable({ providedIn: 'root' })
export class QualityApi {
  private readonly http = inject(HttpClient);
  private legacyApi = false;
  readonly tree = signal<TreeNode[]>(demoTree);
  readonly file = signal<FileDocument | null>(null);
  readonly scan = signal<ScanReport>({ files: [], freshCount: 8, staleCount: 4, missingCount: 3 });
  readonly security = signal<SecurityScanResponse | null>(null);
  readonly connectionState = signal<ApiConnectionState>('connecting');
  readonly connected = computed(() => this.connectionState() === 'live');
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
  readonly repositories = signal<RepositoryRegistration[]>([]);
  readonly selectedRepositoryId = signal('default');
  readonly selectedRepository = computed(() => this.repositories().find(repository => repository.id === this.selectedRepositoryId()) ?? null);
  readonly reviewRuns = signal<ReviewRun[]>([]);
  readonly usage = signal<UsageReport>(emptyUsageReport());
  readonly quotas = signal<QuotaReport>({ at: '', ttlSeconds: 0, providers: [] });
  readonly reviewError = signal('');
  readonly focusedThreadId = signal<string | null>(null);
  private reviewPollTimer: ReturnType<typeof setTimeout> | null = null;

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
      this.repositories.set([{ id: 'default', displayName: 'Default repository', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], archived: false }]);
      this.selectedRepositoryId.set('default');
      console.warn(JSON.stringify({ event: 'qs.repositories.legacy-fallback', reason: this.errorMessage(error) }));
    }
  }

  async selectRepository(id: string): Promise<void> {
    this.selectedRepositoryId.set(id);
    this.connectionState.set('connecting');
    this.file.set(null);
    this.usage.set(emptyUsageReport());
    await this.loadTree();
    await this.loadReviewRuns();
    await this.loadUsage();
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

  async loadTree(): Promise<void> {
    try {
      const [tree, scan, security, inputs] = await Promise.all([
        firstValueFrom(this.http.get<{ nodes: TreeNode[] }>(`${this.repositoryApiBase()}/tree?path=`)),
        firstValueFrom(this.http.get<ScanReport>(`${this.repositoryApiBase()}/scan`)),
        firstValueFrom(this.http.get<SecurityScanResponse>(`${this.repositoryApiBase()}/security/scan`)),
        firstValueFrom(this.http.get<{ kinds: Record<ReviewKind, ResolvedInputs> }>(`${this.repositoryApiBase()}/inputs`)),
      ]);
      this.tree.set(tree.nodes); this.scan.set(scan); this.security.set(security); this.inputs.set(inputs.kinds); this.connectionState.set('live');
      console.info(JSON.stringify({ event: 'qs.data.tree-loaded', nodeCount: tree.nodes.length, source: 'api' }));
    } catch (error) {
      this.security.set(demoSecurity);
      this.connectionState.set('preview');
      console.warn(JSON.stringify({ event: 'qs.data.demo-fallback', reason: error instanceof Error ? error.message : 'API unavailable' }));
    }
    await this.loadHandoverConfiguration();
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

  async loadReviewRuns(): Promise<void> {
    if (!this.connected()) return;
    try {
      const before = new Map(this.reviewRuns().map(run => [run.id, run.state]));
      const result = await firstValueFrom(this.http.get<{ runs: ReviewRun[] }>(`${this.repositoryApiBase()}/review/runs`));
      this.reviewRuns.set(result.runs);
      const completed = result.runs.some(run => ['done', 'failed', 'cancelled'].includes(run.state) && ['queued', 'running'].includes(before.get(run.id) ?? ''));
      if (completed) {
        const openPath = this.file()?.path;
        await this.loadTree();
        if (openPath) await this.loadFile(openPath);
        await Promise.all([this.loadUsage(), this.loadQuotas()]);
      }
      if (result.runs.some(run => run.state === 'queued' || run.state === 'running')) this.scheduleReviewPoll();
    } catch (error) {
      this.reviewError.set(this.errorMessage(error));
    }
  }

  async loadUsage(since?: string, kind?: ReviewKind): Promise<void> {
    if (!this.connected()) return;
    try {
      const params: Record<string, string> = {};
      if (since) params['since'] = since;
      if (kind) params['kind'] = kind;
      this.usage.set(await firstValueFrom(this.http.get<UsageReport>(`${this.repositoryApiBase()}/usage`, { params })));
    } catch (error) {
      this.usage.set(emptyUsageReport());
      console.warn(JSON.stringify({ event: 'qs.usage.unavailable', reason: this.errorMessage(error) }));
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

  async resumeReview(id: string): Promise<void> {
    try {
      const run = await firstValueFrom(this.http.post<ReviewRun>(`${this.repositoryApiBase()}/review/runs/${encodeURIComponent(id)}/resume`, {}));
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
      this.file.set(file); this.connectionState.set('live');
    } catch (error) {
      this.file.set({ path, content: demoFile, metaDocuments: demoMeta, sizeBytes: demoFileSizeBytes, lineEnding: 'lf', encoding: 'utf-8' });
      if (this.connectionState() !== 'live') this.connectionState.set('preview');
      console.warn(JSON.stringify({ event: 'qs.data.file-demo-fallback', path, reason: error instanceof Error ? error.message : 'API unavailable' }));
    } finally { this.loading.set(false); }
  }

  clearFile(): void { this.file.set(null); }

  async createTask(request: HandoverRequest): Promise<HandoverResult> {
    return firstValueFrom(this.http.post<HandoverResult>(`${this.repositoryApiBase()}/handover`, request));
  }

  async mutateThread(request: ThreadMutationRequest): Promise<ReviewThread> {
    const thread = await firstValueFrom(this.http.post<ReviewThread>(`${this.repositoryApiBase()}/threads`, request));
    await this.loadFile(request.path);
    console.info(JSON.stringify({ event: 'qs.thread.mutated', threadId: thread.id, path: request.path, status: thread.status, hasEntry: !!request.body }));
    return thread;
  }

  async mutateFindingState(request: FindingStateMutationRequest): Promise<void> {
    await firstValueFrom(this.http.post(`${this.repositoryApiBase()}/findings/state`, request));
    await Promise.all([this.loadFile(request.path), this.loadTree()]);
    console.info(JSON.stringify({ event: 'qs.finding.state-mutated', fingerprint: request.fingerprint, path: request.path, state: request.state }));
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

  private repositoryApiBase(): string {
    return this.legacyApi ? '/api' : `/api/repos/${encodeURIComponent(this.selectedRepositoryId())}`;
  }
}
