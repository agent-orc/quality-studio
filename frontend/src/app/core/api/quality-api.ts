import { HttpClient, HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { Injectable, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiContext } from './api-context';
import {
  AgentStudioImportResponse, AttackCoverageMatrix, FindingStateMutationRequest, FindingSuppressionMutation,
  FindingSuppressionsResponse, Guideline,
  GuidelineCatalogueEntry, GuidelineDraft, GuidelineImpact, GuidelineTrace, HandoverRequest, HandoverResult, ProjectDashboard,
  QualityRunReport, QualityRunTrendPage, RepositoryRegistration, RepositoryRegistrationRequest,
  RepositoryTransition, ResolvedInputs, ReviewFinding, ReviewKind, ReviewModelRecommendation,
  ReviewPreflight, ReviewRun, ReviewRunCompareResult, ReviewRunRetention, ReviewThread, RiskReport,
  RunReportFormat, ScanReport, ScopeRuleMutation, ScopeRulesResponse, ScopeRuleView,
  SecurityScanResponse, StartReviewRequest, ThreadMutationRequest, TreeLevelResponse, TreeNode,
} from '../models/contracts';
import { FindingsApi } from './findings-api';
import { RepositoriesApi } from './repositories-api';
import { ReviewRunsApi, emptyUsageReport } from './review-runs-api';
import { ScopeApi } from './scope-api';
import { FlatNode, flattenTree } from '../../shared/utils/tree-utils';

const NO_EXPANSION: ReadonlySet<string> = new Set<string>();
/** How long the shell waits before probing an unreachable API again. */
const RECONNECT_INTERVAL_MS = 5_000;
/** How long a repository transition stays visible after its data arrived, to avoid a flicker. */
const TRANSITION_HOLD_MS = 250;
/** Nodes per page of one lazy tree level. */
const TREE_PAGE_LIMIT = 500;
/** Upper bound on the server-side filter answer, so a filter stays a small response. */
const TREE_SEARCH_LIMIT = 200;
/** The explorer follows repository folders rather than the review-unit hierarchy. */
const TREE_VIEW = 'files';

function treeSnapshotKey(repositoryId: string, path = ''): string {
  return `${repositoryId}\0${TREE_VIEW}\0${path}`;
}

/** A loaded hierarchy plus which contract and route produced it. */
interface LoadedTree {
  nodes: TreeNode[];
  etag: string | null;
  schemaVersion: 1 | 2;
  source: 'api' | 'legacy-api';
}

/**
 * The shell's view of one repository: its hierarchy, dashboard, scan, risk, and resolved inputs,
 * plus the orchestration that ties the domain services together (repository selection, refresh
 * after a settled run, reconnect after an outage).
 *
 * Repositories, runs, findings, and scope live in their own services; this facade re-exposes them
 * so every component keeps one collaborator.
 */
@Injectable({ providedIn: 'root' })
export class QualityApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ApiContext);
  private readonly repositoriesApi = inject(RepositoriesApi);
  private readonly runsApi = inject(ReviewRunsApi);
  private readonly findingsApi = inject(FindingsApi);
  private readonly scopeApi = inject(ScopeApi);

  readonly tree = signal<TreeNode[]>([]);
  /**
   * One flattening of the whole tree, shared by every consumer that resolves a path. Callers used
   * to run `flattenTree(..., true)` three or four times per click; this caches it per tree value.
   */
  readonly allNodes = computed(() => flattenTree(this.tree(), NO_EXPANSION, true));
  /**
   * Server-side matches for the explorer filter. The tree only holds the levels that have been
   * expanded, so a filter cannot be answered from `allNodes()` alone.
   */
  readonly treeSearchResults = signal<TreeNode[]>([]);
  /** Exact navigation targets survive changes to the independent explorer filter. */
  private readonly resolvedNodes = signal<TreeNode[]>([]);
  /** Container node ids whose children are in flight, so a row can show that it is loading. */
  readonly treeChildrenLoading = signal(new Set<string>());
  /**
   * Search hits resolve paths too: a deep link into an unexpanded part of the tree is only
   * reachable through them. Loaded nodes win, so an expanded node is never shadowed by its hit.
   */
  readonly nodesByPath = computed(() => new Map([
    ...flattenTree(this.resolvedNodes(), NO_EXPANSION, true),
    ...flattenTree(this.treeSearchResults(), NO_EXPANSION, true),
    ...this.allNodes(),
  ].map(node => [node.path, node])));
  readonly scan = signal<ScanReport>({ files: [], freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0, invalidCount: 0 });
  readonly security = signal<SecurityScanResponse | null>(null);
  readonly attackCoverage = signal<AttackCoverageMatrix | null>(null);
  readonly attackCoverageLoading = signal(false);
  readonly attackCoverageError = signal('');
  readonly risk = signal<RiskReport>({ days: 90, currentCommit: null, rows: [], matrix: [] });
  readonly project = signal<ProjectDashboard | null>(null);
  readonly projectLoading = signal(true);
  readonly projectError = signal('');
  readonly repositoryTransition = signal<RepositoryTransition | null>(null);
  readonly inputs = signal<Partial<Record<ReviewKind, ResolvedInputs>>>({});

  // Connection state, owned by ApiContext.
  readonly connectionState = this.context.connectionState;
  readonly connectionError = this.context.connectionError;
  readonly retryingConnection = signal(false);
  readonly connected = this.context.connected;
  readonly preview = this.context.preview;
  readonly connectionLabel = this.context.connectionLabel;
  readonly selectedRepositoryId = this.context.selectedRepositoryId;

  // Domain state, owned by the services below.
  readonly repositories = this.repositoriesApi.repositories;
  readonly selectedRepository = this.repositoriesApi.selectedRepository;
  readonly reviewRuns = this.runsApi.runs;
  readonly reviewError = this.runsApi.reviewError;
  readonly modelCatalog = this.runsApi.modelCatalog;
  readonly usage = this.runsApi.usage;
  readonly quotas = this.runsApi.quotas;
  readonly file = this.findingsApi.file;
  readonly fileError = this.findingsApi.fileError;
  readonly loading = this.findingsApi.loading;
  readonly focusedThreadId = this.findingsApi.focusedThreadId;
  readonly handoverConfigured = this.findingsApi.handoverConfigured;
  readonly handoverDryRun = this.findingsApi.handoverDryRun;
  readonly findingSuppressions = this.findingsApi.findingSuppressions;
  readonly scopeRules = this.scopeApi.scopeRules;
  readonly guidelines = this.scopeApi.guidelines;
  readonly guidelineCatalogue = this.scopeApi.guidelineCatalogue;
  readonly guidelineTraces = this.scopeApi.guidelineTraces;

  private readonly treeSnapshots = new Map<string, [TreeNode[], string | null]>();
  private readonly projectSnapshots = new Map<string, [ProjectDashboard, string | null]>();
  private repositorySelectionSequence = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectRequest: Promise<void> | null = null;
  private destroyed = false;
  private treeSearchSequence = 0;
  /** One in-flight child request per container, so a double-click does not fetch the level twice. */
  private readonly treeChildrenRequests = new Map<string, Promise<void>>();
  /** Pins every lazy page of a repository to the root snapshot it was cut from. */
  private readonly treeSnapshotEtags = new Map<string, string>();

  constructor() {
    effect(() => { if (this.connectionState() === 'offline') this.scheduleReconnect(); });
    this.runsApi.onRunsSettled = () => this.refreshAfterRun();
    this.runsApi.onUnreachable = () => this.scheduleReconnect();
    this.findingsApi.onFindingsChanged = () => this.loadTree();
    this.scopeApi.onScopeChanged = () => this.loadTree();
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.reconnectTimer !== null) clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    });
  }

  // --- repositories -------------------------------------------------------------------------

  async loadRepositories(preferredId?: string | null): Promise<void> { await this.repositoriesApi.load(preferredId); }
  createRepository(request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> { return this.repositoriesApi.create(request); }
  updateRepository(id: string, request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> { return this.repositoriesApi.update(id, request); }
  archiveRepository(id: string): Promise<void> { return this.repositoriesApi.archive(id); }
  importFromAgentStudio(): Promise<AgentStudioImportResponse> { return this.repositoriesApi.importFromAgentStudio(); }

  async selectRepository(id: string): Promise<void> {
    const started = performance.now();
    const sequence = ++this.repositorySelectionSequence;
    this.context.selectedRepositoryId.set(id);
    this.resolvedNodes.set([]);
    this.context.connectionState.set('connecting');
    this.findingsApi.clearFile();
    this.attackCoverage.set(null);
    this.treeSearchResults.set([]);
    const treeSnapshot = this.treeSnapshots.get(treeSnapshotKey(id))?.[0];
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
      const remaining = Math.max(0, TRANSITION_HOLD_MS - (performance.now() - started));
      setTimeout(() => {
        if (sequence === this.repositorySelectionSequence) this.repositoryTransition.set(null);
      }, remaining);
    });
    console.info(JSON.stringify({ event: 'qs.repository.selected', repositoryId: id }));
  }

  // --- hierarchy and dashboard --------------------------------------------------------------

  async loadTree(repositoryId = this.selectedRepositoryId(), waitForDetails = true, path = ''): Promise<void> {
    const base = this.context.repositoryApiBase(repositoryId);
    const snapshotKey = treeSnapshotKey(repositoryId, path);
    const retained = this.treeSnapshots.get(snapshotKey);
    const detailsLoading = waitForDetails ? this.loadRepositoryDetails(repositoryId) : null;
    try {
      // The root arrives one level at a time over the versioned contract. A path-scoped request
      // keeps the recursive route, which is addressed by path rather than by parent.
      const loaded = path
        ? await this.loadRecursiveTree(base, path, retained?.[1] ?? null)
        : await this.loadRootLevel(base, repositoryId, retained?.[1] ?? null);
      this.treeSnapshots.set(snapshotKey, [loaded.nodes, loaded.etag]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.tree.set(loaded.nodes);
      this.context.markConnected();
      console.info(JSON.stringify({ event: 'qs.data.tree-loaded', schemaVersion: loaded.schemaVersion, nodeCount: loaded.nodes.length, source: loaded.source }));
    } catch (error) {
      if (!this.reuseSnapshot(error, repositoryId, retained) && repositoryId === this.selectedRepositoryId()) {
        // Keep retained real data behind the offline gate; never substitute demonstration data.
        this.context.connectionState.set('offline');
        this.context.connectionError.set(this.errorMessage(error));
        this.scheduleReconnect();
        console.warn(JSON.stringify({
          event: 'qs.data.tree-unavailable',
          repositoryId,
          preview: this.preview(),
          reason: this.errorMessage(error),
        }));
      }
    }
    if (detailsLoading) await detailsLoading;
  }

  /**
   * Fetches one container's children on first expansion. Only the root level is paid for up front;
   * everything below it is requested here, once, and merged into the retained snapshot.
   */
  async loadTreeChildren(node: TreeNode, repositoryId = this.selectedRepositoryId()): Promise<void> {
    if (!(node.hasChildren ?? node.children.length > 0) || node.childrenLoaded || node.children.length > 0) return;
    const snapshotKey = treeSnapshotKey(repositoryId);
    const requestedSnapshot = this.treeSnapshotEtags.get(snapshotKey);
    const key = `${snapshotKey}\0${requestedSnapshot ?? ''}\0${node.id}`;
    const existing = this.treeChildrenRequests.get(key);
    if (existing) return existing;
    this.treeChildrenLoading.update(current => new Set([...current, node.id]));
    const request = (async () => {
      try {
        const level = await this.loadTreeLevel(this.context.repositoryApiBase(repositoryId), node.id, repositoryId);
        // A refresh may have installed a newer tree while these children loaded.
        if (requestedSnapshot !== this.treeSnapshotEtags.get(snapshotKey)) return;
        const retained = this.treeSnapshots.get(snapshotKey);
        const current = repositoryId === this.selectedRepositoryId() ? this.tree() : retained?.[0] ?? [];
        const updated = this.replaceTreeChildren(current, node.id, level.nodes);
        this.treeSnapshots.set(snapshotKey, [updated, retained?.[1] ?? null]);
        if (repositoryId === this.selectedRepositoryId()) {
          this.tree.set(updated);
          // Dashboard and restored-URL containers may only exist in the resolution
          // cache, outside the expanded tree. Keep their children visible there too.
          this.resolvedNodes.update(nodes => this.replaceTreeChildren(nodes, node.id, level.nodes));
          // Startup deep links and explorer search selections can live only in the
          // search result set. Their identity must receive the same loaded children.
          this.treeSearchResults.update(nodes => this.replaceTreeChildren(nodes, node.id, level.nodes));
        }
        console.info(JSON.stringify({ event: 'qs.data.tree-children-loaded', parentId: node.id, nodeCount: level.nodes.length, source: 'api' }));
      } catch (error) {
        console.warn(JSON.stringify({ event: 'qs.data.tree-children-failed', parentId: node.id, reason: this.errorMessage(error) }));
      } finally {
        this.treeChildrenRequests.delete(key);
        if (![...this.treeChildrenRequests.keys()].some(pending => pending.endsWith(`\0${node.id}`))) {
          this.treeChildrenLoading.update(current => {
            const next = new Set(current);
            next.delete(node.id);
            return next;
          });
        }
      }
    })();
    this.treeChildrenRequests.set(key, request);
    return request;
  }

  /**
   * Filters across the whole repository, not just the expanded levels. The answer is bounded by
   * the server, so a filter over a large repository stays a small response.
   */
  async searchTree(query: string, repositoryId = this.selectedRepositoryId()): Promise<void> {
    const normalized = query.trim();
    const sequence = ++this.treeSearchSequence;
    if (!normalized) {
      this.treeSearchResults.set([]);
      return;
    }
    try {
      const page = await firstValueFrom(this.http.get<TreeLevelResponse>(
        `${this.context.repositoryApiBase(repositoryId)}/tree/v2/search`,
        { params: { query: normalized, limit: String(TREE_SEARCH_LIMIT), view: TREE_VIEW } }));
      if (sequence !== this.treeSearchSequence || repositoryId !== this.selectedRepositoryId()) return;
      this.treeSearchResults.set(page.nodes.map(node => this.normalizeTreeNode(node, false)));
    } catch (error) {
      if (sequence !== this.treeSearchSequence || repositoryId !== this.selectedRepositoryId()) return;
      this.treeSearchResults.set([]);
      console.warn(JSON.stringify({ event: 'qs.data.tree-search-failed', reason: this.errorMessage(error) }));
    }
  }

  /** Resolve a dashboard/file link without changing the explorer's filter or selection. */
  async resolveNode(path: string, repositoryId = this.selectedRepositoryId()): Promise<FlatNode | undefined> {
    if (repositoryId !== this.selectedRepositoryId()) return undefined;
    const existing = this.nodesByPath().get(path);
    if (existing) return existing;
    try {
      const page = await firstValueFrom(this.http.get<TreeLevelResponse>(
        this.context.repositoryApiBase(repositoryId) + '/tree/v2/search',
        { params: { query: path, limit: String(TREE_SEARCH_LIMIT), view: TREE_VIEW } }));
      if (repositoryId !== this.selectedRepositoryId()) return undefined;
      const match = flattenTree(page.nodes.map(node => this.normalizeTreeNode(node, false)), NO_EXPANSION, true)
        .find(node => node.path === path);
      if (!match) return undefined;
      this.resolvedNodes.update(nodes => [...nodes.filter(node => node.path !== path), match].slice(-TREE_SEARCH_LIMIT));
      return this.nodesByPath().get(path);
    } catch (error) {
      console.warn(JSON.stringify({ event: 'qs.data.navigation-target-unavailable', repositoryId, path, reason: this.errorMessage(error) }));
      return undefined;
    }
  }

  /** Restore the registry and current repository together after an outage. */
  retryConnection(): Promise<void> {
    if (this.reconnectRequest) return this.reconnectRequest;
    if (this.reconnectTimer !== null) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    this.retryingConnection.set(true);
    this.reconnectRequest = this.restoreConnection().finally(() => {
      this.reconnectRequest = null;
      this.retryingConnection.set(false);
      if (!this.connected()) this.scheduleReconnect();
    });
    return this.reconnectRequest;
  }

  private async restoreConnection(): Promise<void> {
    const previousId = this.selectedRepositoryId();
    if (!await this.repositoriesApi.load(previousId) || this.destroyed) return;
    const repositoryId = this.selectedRepositoryId();
    if (repositoryId !== previousId) {
      this.resolvedNodes.set([]);
      this.tree.set([]);
      this.project.set(null);
      this.findingsApi.clearFile();
    }
    await Promise.all([this.loadTree(repositoryId, false), this.loadProjectDashboard(repositoryId)]);
    if (!this.connected() || this.destroyed) return;
    console.info(JSON.stringify({ event: 'qs.data.reconnected', repositoryId }));
    await Promise.all([
      this.loadRepositoryDetails(repositoryId), this.loadModelCatalog(),
      this.loadReviewRuns(repositoryId), this.loadUsage(undefined, undefined, repositoryId), this.loadQuotas(),
      this.file()?.path ? this.loadFile(this.file()!.path) : Promise.resolve(),
    ]);
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
        `${this.context.repositoryApiBase(repositoryId)}/project`,
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

  async loadAttackCoverage(scope = 'src/QualityStudio.Api'): Promise<AttackCoverageMatrix> {
    this.attackCoverageLoading.set(true);
    this.attackCoverageError.set('');
    try {
      const matrix = await firstValueFrom(this.http.get<AttackCoverageMatrix>(
        `${this.context.repositoryApiBase()}/security/attack-coverage`, { params: { path: scope } }));
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

  /** Resolves a repository path against the cached flattening. */
  nodeAt(path: string): FlatNode | undefined { return this.nodesByPath().get(path); }

  // --- review runs --------------------------------------------------------------------------

  startReview(request: StartReviewRequest): Promise<ReviewRun> { return this.runsApi.start(request); }
  estimateReview(request: StartReviewRequest): Promise<ReviewPreflight> { return this.runsApi.estimate(request); }
  loadModelCatalog(): Promise<void> { return this.runsApi.loadModelCatalog(); }
  defaultModelRecommendation(kind: ReviewKind, level: string, files: number): Promise<ReviewModelRecommendation | null> {
    return this.runsApi.defaultModelRecommendation(kind, level, files);
  }
  loadReviewRuns(repositoryId = this.selectedRepositoryId()): Promise<void> { return this.runsApi.load(repositoryId); }
  cancelReview(id: string): Promise<void> { return this.runsApi.cancel(id); }
  pauseReview(id: string): Promise<void> { return this.runsApi.pause(id); }
  resumeReview(id: string, cap: { tokenCap?: number | null; costCap?: number | null } = {}): Promise<void> { return this.runsApi.resume(id, cap); }
  loadRunReport(id: string): Promise<QualityRunReport> { return this.runsApi.loadReport(id); }
  loadRunTrend(kind: ReviewKind, scopeUnitId: string, level: string, cursor?: string): Promise<QualityRunTrendPage> {
    return this.runsApi.loadTrend(kind, scopeUnitId, level, cursor);
  }
  compareRuns(baselineId: string, candidateId: string): Promise<ReviewRunCompareResult> { return this.runsApi.compare(baselineId, candidateId); }
  loadRunRetention(): Promise<ReviewRunRetention> { return this.runsApi.loadRetention(); }
  loadPinnedRunIds(): Promise<string[]> { return this.runsApi.loadPinnedRunIds(); }
  pinRun(id: string): Promise<string[]> { return this.runsApi.pin(id); }
  unpinRun(id: string): Promise<string[]> { return this.runsApi.unpin(id); }
  runReportUrl(id: string, format: RunReportFormat): string { return this.runsApi.reportUrl(id, format); }
  runReportFileName(id: string, format: RunReportFormat): string { return this.runsApi.reportFileName(id, format); }
  repositoryReportUrl(format: RunReportFormat = 'html'): string { return this.runsApi.repositoryReportUrl(format); }
  loadUsage(since?: string, kind?: ReviewKind, repositoryId = this.selectedRepositoryId()): Promise<void> {
    return this.runsApi.loadUsage(since, kind, repositoryId);
  }
  loadQuotas(): Promise<void> { return this.runsApi.loadQuotas(); }

  // --- findings -----------------------------------------------------------------------------

  loadFile(path: string): Promise<void> { return this.findingsApi.loadFile(path); }
  clearFile(): void { this.findingsApi.clearFile(); }
  mutateThread(request: ThreadMutationRequest): Promise<ReviewThread> { return this.findingsApi.mutateThread(request); }
  mutateFindingState(request: FindingStateMutationRequest): Promise<ReviewFinding | null> { return this.findingsApi.mutateFindingState(request); }
  createTask(request: HandoverRequest): Promise<HandoverResult> { return this.findingsApi.createTask(request); }
  loadFindingSuppressions(): Promise<FindingSuppressionsResponse> { return this.findingsApi.loadFindingSuppressions(); }
  addFindingSuppression(request: FindingSuppressionMutation): Promise<ReviewFinding | null> { return this.findingsApi.addFindingSuppression(request); }
  deleteFindingSuppression(id: string, expectedRevision: number): Promise<void> { return this.findingsApi.deleteFindingSuppression(id, expectedRevision); }

  // --- scope and guidelines -----------------------------------------------------------------

  loadScopeRules(): Promise<ScopeRulesResponse> { return this.scopeApi.loadScopeRules(); }
  previewScopeRule(request: ScopeRuleMutation): Promise<ScopeRuleView> { return this.scopeApi.previewScopeRule(request); }
  addScopeRule(request: ScopeRuleMutation): Promise<ScopeRulesResponse> { return this.scopeApi.addScopeRule(request); }
  updateScopeRule(index: number, request: ScopeRuleMutation): Promise<ScopeRulesResponse> { return this.scopeApi.updateScopeRule(index, request); }
  deleteScopeRule(index: number): Promise<ScopeRulesResponse> { return this.scopeApi.deleteScopeRule(index); }
  createGuideline(draft: GuidelineDraft): Promise<Guideline> { return this.scopeApi.createGuideline(draft); }
  updateGuideline(existingId: string, draft: GuidelineDraft): Promise<Guideline> { return this.scopeApi.updateGuideline(existingId, draft); }
  deleteGuideline(id: string): Promise<void> { return this.scopeApi.deleteGuideline(id); }
  installGuideline(catalogueId: string): Promise<Guideline> { return this.scopeApi.installGuideline(catalogueId); }
  guidelineImpact(guideline: GuidelineDraft, samplePaths: string[], kind: ReviewKind): Promise<GuidelineImpact> {
    return this.scopeApi.guidelineImpact(guideline, samplePaths, kind);
  }

  errorMessage(error: unknown): string { return this.context.errorMessage(error); }

  // --- orchestration ------------------------------------------------------------------------

  /** A settled run rewrites review metadata, so the hierarchy, dashboard, and open file re-read. */
  private async refreshAfterRun(): Promise<void> {
    const openPath = this.file()?.path;
    await this.loadTree();
    void this.loadProjectDashboard();
    if (openPath) await this.loadFile(openPath);
  }

  /**
   * Re-probes an unreachable API until it answers, then resumes the work that stopped with it.
   * Without this the shell stayed in preview for the rest of the session once a request failed.
   */
  private scheduleReconnect(): void {
    if (this.destroyed || this.reconnectTimer !== null || this.reconnectRequest || this.connected()) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.retryConnection();
    }, RECONNECT_INTERVAL_MS);
  }

  private async loadRepositoryDetails(repositoryId: string): Promise<void> {
    const base = this.context.repositoryApiBase(repositoryId);
    try {
      const [scan, inputs, guidelines, risk, suppressions] = await Promise.all([
        firstValueFrom(this.http.get<ScanReport>(`${base}/scan`)),
        firstValueFrom(this.http.get<{ kinds: Record<ReviewKind, ResolvedInputs> }>(`${base}/inputs`)),
        firstValueFrom(this.http.get<{ guidelines: Guideline[]; catalogue: GuidelineCatalogueEntry[]; traces: GuidelineTrace[] }>(`${base}/guidelines`)),
        firstValueFrom(this.http.get<RiskReport>(`${base}/risk?days=90`)),
        firstValueFrom(this.http.get<FindingSuppressionsResponse>(`${base}/findings/suppressions`)),
      ]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.scan.set(scan);
      this.inputs.set(inputs.kinds);
      this.guidelines.set(guidelines.guidelines);
      this.guidelineCatalogue.set(guidelines.catalogue);
      this.guidelineTraces.set(guidelines.traces);
      this.risk.set(risk);
      this.findingSuppressions.set(suppressions);
      await this.findingsApi.loadHandoverConfiguration();
    } catch (error) {
      if (repositoryId === this.selectedRepositoryId()) {
        console.warn(JSON.stringify({ event: 'qs.repository.details-unavailable', repositoryId, reason: this.errorMessage(error) }));
      }
    }
  }

  /**
   * The root over the versioned contract, falling back to the recursive route for a server that
   * predates it. A 304 is not a failure to fall back from — it travels on to `reuseSnapshot`.
   */
  private async loadRootLevel(base: string, repositoryId: string, conditionalEtag: string | null): Promise<LoadedTree> {
    try {
      const level = await this.loadTreeLevel(base, null, repositoryId, conditionalEtag);
      return { ...level, schemaVersion: 2, source: 'api' };
    } catch (error) {
      if (!(error instanceof HttpErrorResponse) || error.status !== 404) throw error;
      const legacy = await this.loadRecursiveTree(base, '', null);
      return {
        nodes: legacy.nodes.map(node => this.normalizeTreeNode(node, true)),
        etag: legacy.etag,
        schemaVersion: 1,
        source: 'legacy-api',
      };
    }
  }

  /** The recursive route: the whole subtree under `path` in one response. */
  private async loadRecursiveTree(base: string, path: string, conditionalEtag: string | null): Promise<LoadedTree> {
    const response = await firstValueFrom(this.http.get<{ nodes: TreeNode[] }>(
      `${base}/tree?path=${encodeURIComponent(path)}`,
      { params: { view: TREE_VIEW }, observe: 'response', headers: conditionalEtag ? { 'If-None-Match': conditionalEtag } : undefined }));
    return { nodes: response.body!.nodes, etag: response.headers.get('ETag'), schemaVersion: 1, source: 'api' };
  }

  /**
   * One level of the versioned contract, following the cursor until the level is complete. Only the
   * first page is conditional, so an unchanged level costs one 304 instead of its payload.
   */
  private async loadTreeLevel(
    base: string,
    parentId: string | null,
    repositoryId: string,
    conditionalEtag: string | null = null,
  ): Promise<{ nodes: TreeNode[]; etag: string | null }> {
    const nodes: TreeNode[] = [];
    let cursor: string | null = null;
    let etag: string | null = null;
    const snapshotKey = treeSnapshotKey(repositoryId);
    // A refresh must ask for the latest root. Only children and subsequent pages
    // reuse its immutable snapshot, otherwise a refresh can retain an old layout.
    let snapshotEtag = parentId ? this.treeSnapshotEtags.get(snapshotKey) : undefined;
    do {
      const params: Record<string, string> = { limit: String(TREE_PAGE_LIMIT), view: TREE_VIEW };
      if (parentId) params['parentId'] = parentId;
      // Every page of a level is cut from the same immutable snapshot as its root.
      if (snapshotEtag) params['snapshot'] = snapshotEtag;
      if (cursor) params['cursor'] = cursor;
      // Annotated because `cursor` is assigned from the response it is also a parameter of.
      const response: HttpResponse<TreeLevelResponse> = await firstValueFrom(
        this.http.get<TreeLevelResponse>(`${base}/tree/v2`, {
          params,
          observe: 'response',
          headers: cursor === null && conditionalEtag ? { 'If-None-Match': conditionalEtag } : undefined,
        }));
      const page: TreeLevelResponse = response.body!;
      if (page.schemaVersion !== 2 || !Array.isArray(page.nodes)) throw new Error('Unsupported tree response.');
      if (page.snapshotEtag) {
        snapshotEtag = page.snapshotEtag;
        // A late child response must not replace a newer root's snapshot.
        if (parentId === null) this.treeSnapshotEtags.set(snapshotKey, page.snapshotEtag);
      }
      if (cursor === null) etag = response.headers.get('ETag');
      nodes.push(...page.nodes.map(node => this.normalizeTreeNode(node, false)));
      cursor = page.nextCursor;
    } while (cursor);
    return { nodes, etag };
  }

  /**
   * States for every node whether it has children and whether they are present, so a row can tell
   * "no children" from "children not fetched yet". A recursive response has them all by definition.
   */
  private normalizeTreeNode(node: TreeNode, legacy: boolean): TreeNode {
    const children = (node.children ?? []).map(child => this.normalizeTreeNode(child, legacy));
    const hasChildren = node.hasChildren ?? children.length > 0;
    return {
      ...node,
      hasChildren,
      childCount: node.childCount ?? children.length,
      childrenLoaded: legacy || children.length > 0 || !hasChildren,
      children,
    };
  }

  /** Rebuilds only the branch down to `parentId`; every untouched node keeps its identity. */
  private replaceTreeChildren(nodes: TreeNode[], parentId: string, children: TreeNode[]): TreeNode[] {
    return nodes.map(node => {
      if (node.id === parentId) return { ...node, children, childrenLoaded: true, childCount: children.length };
      if (!node.children.length) return node;
      const updated = this.replaceTreeChildren(node.children, parentId, children);
      return updated.some((child, index) => child !== node.children[index]) ? { ...node, children: updated } : node;
    });
  }

  private reuseSnapshot(error: unknown, repositoryId: string, retained: unknown): boolean {
    if (!retained || !this.context.notModified(error)) return false;
    if (repositoryId === this.selectedRepositoryId()) {
      this.context.markConnected();
      if (this.repositoryTransition()?.repositoryId === repositoryId) this.repositoryTransition.set(null);
    }
    return true;
  }
}
