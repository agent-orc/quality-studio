import { HttpClient, HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { Injectable, DestroyRef, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiContext } from './api-context';
import {
  AgentStudioImportResponse, AttackCoverageMatrix, FindingStateMutationRequest, Guideline,
  GuidelineCatalogueEntry, GuidelineDraft, GuidelineImpact, GuidelineTrace, HandoverRequest, HandoverResult, ProjectDashboard,
  QualityRunReport, QualityRunTrendPage, RepositoryRegistration, RepositoryRegistrationRequest,
  RepositoryTransition, ResolvedInputs, ReviewFinding, ReviewKind, ReviewModelRecommendation,
  ReviewPreflight, ReviewRun, ReviewRunCompareResult, ReviewRunRetention, ReviewThread, RiskReport,
  RunReportFormat, ScanReport, ScopeRuleMutation, ScopeRulesResponse, ScopeRuleView,
  SecurityScanResponse, StartReviewRequest, ThreadMutationRequest, TreeLevelResponse, TreeNode,
} from './contracts';
import { FindingsApi } from './findings-api';
import { RepositoriesApi } from './repositories-api';
import { ReviewRunsApi, emptyUsageReport } from './review-runs-api';
import { ScopeApi } from './scope-api';
import { FlatNode, flattenTree } from './tree-utils';

const NO_EXPANSION: ReadonlySet<string> = new Set<string>();
/** How long the shell waits before probing an unreachable API again. */
const RECONNECT_INTERVAL_MS = 5_000;
/** How long a repository transition stays visible after its data arrived, to avoid a flicker. */
const TRANSITION_HOLD_MS = 250;
/** Nodes per page of one lazy tree level. */
const TREE_PAGE_LIMIT = 500;
/** Upper bound on the server-side filter answer, so a filter stays a small response. */
const TREE_SEARCH_LIMIT = 200;

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
  /** Container node ids whose children are in flight, so a row can show that it is loading. */
  readonly treeChildrenLoading = signal(new Set<string>());
  /**
   * Search hits resolve paths too: a deep link into an unexpanded part of the tree is only
   * reachable through them. Loaded nodes win, so an expanded node is never shadowed by its hit.
   */
  readonly nodesByPath = computed(() => new Map([
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
  readonly scopeRules = this.scopeApi.scopeRules;
  readonly guidelines = this.scopeApi.guidelines;
  readonly guidelineCatalogue = this.scopeApi.guidelineCatalogue;
  readonly guidelineTraces = this.scopeApi.guidelineTraces;

  private readonly treeSnapshots = new Map<string, [TreeNode[], string | null]>();
  private readonly projectSnapshots = new Map<string, [ProjectDashboard, string | null]>();
  private repositorySelectionSequence = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private treeSearchSequence = 0;
  /** One in-flight child request per container, so a double-click does not fetch the level twice. */
  private readonly treeChildrenRequests = new Map<string, Promise<void>>();
  /** Pins every lazy page of a repository to the root snapshot it was cut from. */
  private readonly treeSnapshotEtags = new Map<string, string>();

  constructor() {
    this.runsApi.onRunsSettled = () => this.refreshAfterRun();
    this.runsApi.onUnreachable = () => this.scheduleReconnect();
    this.findingsApi.onFindingsChanged = () => this.loadTree();
    this.scopeApi.onScopeChanged = () => this.loadTree();
    inject(DestroyRef).onDestroy(() => {
      if (this.reconnectTimer !== null) clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    });
  }

  // --- repositories -------------------------------------------------------------------------

  loadRepositories(preferredId?: string | null): Promise<void> { return this.repositoriesApi.load(preferredId); }
  createRepository(request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> { return this.repositoriesApi.create(request); }
  updateRepository(id: string, request: RepositoryRegistrationRequest): Promise<RepositoryRegistration> { return this.repositoriesApi.update(id, request); }
  archiveRepository(id: string): Promise<void> { return this.repositoriesApi.archive(id); }
  importFromAgentStudio(): Promise<AgentStudioImportResponse> { return this.repositoriesApi.importFromAgentStudio(); }

  async selectRepository(id: string): Promise<void> {
    const started = performance.now();
    const sequence = ++this.repositorySelectionSequence;
    this.context.selectedRepositoryId.set(id);
    this.context.connectionState.set('connecting');
    this.findingsApi.clearFile();
    this.attackCoverage.set(null);
    this.treeSearchResults.set([]);
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
    const snapshotKey = `${repositoryId}\0${path}`;
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
      this.context.connectionState.set('live');
      this.context.connectionError.set('');
      console.info(JSON.stringify({ event: 'qs.data.tree-loaded', schemaVersion: loaded.schemaVersion, nodeCount: loaded.nodes.length, source: loaded.source }));
    } catch (error) {
      if (!this.reuseSnapshot(error, repositoryId, retained) && repositoryId === this.selectedRepositoryId()) {
        // Only a request that never reached the API earns preview data; a reachable API that
        // answered with an error keeps the tree empty and states the reason.
        if (this.context.unreachable(error)) {
          this.context.connectionState.set('preview');
          const fixtures = await this.findingsApi.previewFixtures();
          if (fixtures && repositoryId === this.selectedRepositoryId()) this.tree.set(fixtures.tree);
        } else {
          this.context.connectionState.set('offline');
        }
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
    const key = `${repositoryId}\0${node.id}`;
    const existing = this.treeChildrenRequests.get(key);
    if (existing) return existing;
    this.treeChildrenLoading.update(current => new Set([...current, node.id]));
    const request = (async () => {
      try {
        const level = await this.loadTreeLevel(this.context.repositoryApiBase(repositoryId), node.id, repositoryId);
        const snapshotKey = `${repositoryId}\0`;
        const retained = this.treeSnapshots.get(snapshotKey);
        const current = repositoryId === this.selectedRepositoryId() ? this.tree() : retained?.[0] ?? [];
        const updated = this.replaceTreeChildren(current, node.id, level.nodes);
        this.treeSnapshots.set(snapshotKey, [updated, retained?.[1] ?? null]);
        if (repositoryId === this.selectedRepositoryId()) this.tree.set(updated);
        console.info(JSON.stringify({ event: 'qs.data.tree-children-loaded', parentId: node.id, nodeCount: level.nodes.length, source: 'api' }));
      } catch (error) {
        console.warn(JSON.stringify({ event: 'qs.data.tree-children-failed', parentId: node.id, reason: this.errorMessage(error) }));
      } finally {
        this.treeChildrenLoading.update(current => {
          const next = new Set(current);
          next.delete(node.id);
          return next;
        });
        this.treeChildrenRequests.delete(key);
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
        { params: { query: normalized, limit: String(TREE_SEARCH_LIMIT) } }));
      if (sequence !== this.treeSearchSequence || repositoryId !== this.selectedRepositoryId()) return;
      this.treeSearchResults.set(page.nodes.map(node => this.normalizeTreeNode(node, false)));
    } catch (error) {
      if (sequence !== this.treeSearchSequence || repositoryId !== this.selectedRepositoryId()) return;
      this.treeSearchResults.set([]);
      console.warn(JSON.stringify({ event: 'qs.data.tree-search-failed', reason: this.errorMessage(error) }));
    }
  }

  /** Re-runs the request that decides whether the API is answering. */
  async retryConnection(): Promise<void> {
    this.context.connectionState.set('connecting');
    await this.loadTree(this.selectedRepositoryId(), false);
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
    if (this.reconnectTimer !== null || this.connected()) return;
    this.reconnectTimer = setTimeout(async () => {
      this.reconnectTimer = null;
      await this.loadTree();
      if (!this.connected()) {
        this.scheduleReconnect();
        return;
      }
      console.info(JSON.stringify({ event: 'qs.data.reconnected', repositoryId: this.selectedRepositoryId() }));
      await Promise.all([this.loadReviewRuns(), this.loadUsage(), this.loadQuotas()]);
    }, RECONNECT_INTERVAL_MS);
  }

  private async loadRepositoryDetails(repositoryId: string): Promise<void> {
    const base = this.context.repositoryApiBase(repositoryId);
    try {
      const [scan, inputs, guidelines, risk] = await Promise.all([
        firstValueFrom(this.http.get<ScanReport>(`${base}/scan`)),
        firstValueFrom(this.http.get<{ kinds: Record<ReviewKind, ResolvedInputs> }>(`${base}/inputs`)),
        firstValueFrom(this.http.get<{ guidelines: Guideline[]; catalogue: GuidelineCatalogueEntry[]; traces: GuidelineTrace[] }>(`${base}/guidelines`)),
        firstValueFrom(this.http.get<RiskReport>(`${base}/risk?days=90`)),
      ]);
      if (repositoryId !== this.selectedRepositoryId()) return;
      this.scan.set(scan);
      this.inputs.set(inputs.kinds);
      this.guidelines.set(guidelines.guidelines);
      this.guidelineCatalogue.set(guidelines.catalogue);
      this.guidelineTraces.set(guidelines.traces);
      this.risk.set(risk);
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
      { observe: 'response', headers: conditionalEtag ? { 'If-None-Match': conditionalEtag } : undefined }));
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
    do {
      const params: Record<string, string> = { limit: String(TREE_PAGE_LIMIT) };
      if (parentId) params['parentId'] = parentId;
      // Every page of a level is cut from the same immutable snapshot as its root.
      const snapshotEtag = this.treeSnapshotEtags.get(repositoryId);
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
      if (page.snapshotEtag) this.treeSnapshotEtags.set(repositoryId, page.snapshotEtag);
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
      this.context.connectionState.set('live');
      if (this.repositoryTransition()?.repositoryId === repositoryId) this.repositoryTransition.set(null);
    }
    return true;
  }
}
