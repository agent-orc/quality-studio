import { GuidelineEditorState } from '../features/settings/guideline-dialog/guideline-editor-state';
import { RepositoryEditorState } from '../features/repositories/repository-dialog/repository-editor-state';
import { ConfirmationState } from '../shared/dialog/confirmation-state';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { WorkspaceLayoutState, ResizablePane } from './workspace-layout-state';
import { ApiConnectionState } from '../shared/ui/api-connection-state/api-connection-state';
import { ApiAccess } from '../core/api/api-access';
import { AgentStudioImport } from '../features/repositories/agent-studio-import/agent-studio-import';
import { ApiAccessDialog } from '../features/settings/api-access-dialog/api-access-dialog';
import { ConfirmDialog } from '../shared/dialog/confirm-dialog';
import { GuidelineDialog } from '../features/settings/guideline-dialog/guideline-dialog';
import { GuidelineForm } from '../features/settings/guideline-dialog/guideline-form';
import { AttackCoverage } from '../features/security/attack-coverage/attack-coverage';
import { Editor } from '../features/code/editor/editor';
import { Explorer } from '../features/code/explorer/explorer';
import { QualityApi } from '../core/api/quality-api';
import { AgentStudioImportResponse, Guideline, QuotaProvider, RepositoryRegistration, RepositoryRegistrationRequest, ReviewFinding, ReviewKind } from '../core/models/contracts';
import { ReviewPanel } from '../features/reviews/review-panel/review-panel';
import { ReviewActions } from '../features/reviews/review-actions/review-actions';
import { ProjectDashboardView } from '../features/dashboard/project-dashboard/project-dashboard';
import { UsageHistory } from '../features/reviews/usage-history/usage-history';
import { readFindingRoute, writeFindingRoute } from '../core/navigation/review-navigation';
import { reportUrlPreviewNavigation } from '../core/navigation/url-preview-embed';
import { formatTokenCount } from '../shared/utils/format';
import { RepositoryDialog } from '../features/repositories/repository-dialog/repository-dialog';

const LAST_REPOSITORY_STORAGE_KEY = 'qs-last-repository';
/** Collapses a salvo of position changes into one history write. */
const URL_SYNC_DEBOUNCE_MS = 120;
interface ShellPosition {
  repository: string;
  path: string;
  kind: ReviewKind;
  fingerprint: string | null;
  locationIndex: number;
}
@Component({
  selector: 'app-root',
  providers: [WorkspaceLayoutState, ConfirmationState, GuidelineEditorState, RepositoryEditorState],
  imports: [ApiConnectionState, Explorer, Editor, ReviewPanel, ReviewActions, AttackCoverage, UsageHistory, ProjectDashboardView, RepositoryDialog, ApiAccessDialog, ConfirmDialog, GuidelineDialog, AgentStudioImport],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:resize)': 'onResize()',
    '(window:keydown)': 'onKeydown($event)',
    '(window:popstate)': 'onPopState()',
    '(window:pointermove)': 'onDragMove($event)',
    '(window:pointerup)': 'onDragEnd()',
    '(window:pointercancel)': 'onDragEnd()',
  },
})
export class App implements OnDestroy {
  private readonly layout = inject(WorkspaceLayoutState);
  private readonly confirmations = inject(ConfirmationState);
  readonly guidelines = inject(GuidelineEditorState);
  readonly repositories = inject(RepositoryEditorState);
  readonly api = inject(QualityApi);
  readonly access = inject(ApiAccess);
  // Queried by template reference, not by type, so the Explorer stays a deferred chunk.
  readonly explorer = viewChild<Explorer>('explorerPane');
  readonly usageButton = viewChild.required<ElementRef<HTMLButtonElement>>('usageButton');
  readonly embedded = signal(this.detectEmbedded());
  readonly theme = signal<'dark' | 'light'>((new URLSearchParams(location.search).get('theme') as 'dark' | 'light') || (localStorage.getItem('qs-theme') as 'dark' | 'light') || 'dark');
  readonly selected = signal(new URLSearchParams(location.search).get('path') || '.');
  readonly activeKind = signal<ReviewKind>((new URLSearchParams(location.search).get('kind') as ReviewKind) || 'code');
  readonly selectedFinding = signal<ReviewFinding | null>(null);
  private readonly initialFindingRoute = readFindingRoute(location.search);
  readonly selectedFindingFingerprint = signal<string | null>(this.initialFindingRoute.fingerprint);
  readonly selectedLocationIndex = signal(this.initialFindingRoute.locationIndex);
  readonly reviewFocusRequest = signal(0);
  readonly repositoryMenuOpen = signal(false);
  readonly repositoryDialogOpen = this.repositories.repositoryDialogOpen;
  readonly editingRepositoryId = this.repositories.editingRepositoryId;
  readonly repositoryError = this.repositories.repositoryError;
  readonly repositoryTokenCapError = this.repositories.repositoryTokenCapError;
  readonly repositorySaving = this.repositories.repositorySaving;
  readonly agentStudioImportDialogOpen = signal(false);
  readonly agentStudioImporting = signal(false);
  readonly agentStudioImportResult = signal<AgentStudioImportResponse | null>(null);
  readonly agentStudioImportError = signal('');
  readonly guidelineDialogOpen = this.guidelines.guidelineDialogOpen;
  readonly editingGuidelineId = this.guidelines.editingGuidelineId;
  readonly guidelineError = this.guidelines.guidelineError;
  readonly guidelineSaving = this.guidelines.guidelineSaving;
  readonly guidelineDryRunning = this.guidelines.guidelineDryRunning;
  readonly guidelineImpact = this.guidelines.guidelineImpact;
  readonly attackCoverageDialogOpen = signal(false);
  readonly usageHistoryOpen = signal(false);
  readonly apiAccessDialogOpen = signal(false);
  readonly apiAccessRejected = signal(false);
  readonly confirmation = this.confirmations.pending;
  readonly viewportHeight = signal(typeof window === 'undefined' ? 1000 : window.innerHeight);
  readonly selectedNode = computed(() => this.api.nodeAt(this.selected())
    ?? (this.selected() === '.' ? this.api.allNodes().find(node => node.level === 'project') : undefined));
  readonly explorerSelectedPath = computed(() => this.selected() === '.' ? this.selectedNode()?.path ?? '.' : this.selected());
  readonly isProjectView = computed(() => this.selected() === '.' || this.selectedNode()?.level === 'project');
  readonly editingRepository = computed(() => this.api.repositories().find(repository => repository.id === this.editingRepositoryId()) ?? null);
  readonly usageTotalLabel = computed(() => new Intl.NumberFormat('en-US').format(
    this.api.usage().inputTokens + this.api.usage().outputTokens));
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  get repositoryForm(): RepositoryRegistrationRequest { return this.repositories.repositoryForm; }
  set repositoryForm(value: RepositoryRegistrationRequest) { this.repositories.repositoryForm = value; }
  get repositoryTokenCapText(): string { return this.repositories.repositoryTokenCapText; }
  set repositoryTokenCapText(value: string) { this.repositories.repositoryTokenCapText = value; }
  get guidelineForm(): GuidelineForm { return this.guidelines.guidelineForm; }

  readonly explorerVisible = this.layout.explorerVisible;
  readonly reviewVisible = this.layout.reviewVisible;
  readonly explorerWidth = this.layout.explorerWidth;
  readonly reviewWidth = this.layout.reviewWidth;
  readonly dragging = this.layout.dragging;
  readonly gridTemplateColumns = this.layout.gridTemplateColumns;
  private urlSyncTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingPosition: ShellPosition | null = null;
  /** The position the current history entry represents, so refinements only replace it. */
  private historyPosition: { repository: string; path: string } | null = null;
  private readonly quotaRefreshTimer: ReturnType<typeof setInterval>;

  constructor() {
    effect(() => document.documentElement.dataset['theme'] = this.theme());
    let awaitingReconnect = false;
    effect(() => {
      const connection = this.api.connectionState();
      if (connection === 'offline' || connection === 'preview') awaitingReconnect = true;
      if (connection !== 'live' || !awaitingReconnect) return;
      awaitingReconnect = false;
      // Startup may have stopped before resolving the URL destination. Recover it once
      // when the API returns; ordinary tree updates must not reopen the current editor.
      untracked(() => {
        const path = this.selected();
        if (path !== '.' && this.api.file()?.path !== path) {
          void this.open(path, false, false, !!this.selectedFindingFingerprint());
        }
      });
    });
    // Deep-linkable position: mirror the selected path and review kind into the
    // URL, and report every navigation to an embedding Studio preview so its
    // address bar stays current (url-preview-embed contract). Writes are
    // debounced: selecting findings with the keyboard used to fire one
    // history write per event, which Safari throttles.
    effect(() => {
      const position: ShellPosition = {
        repository: this.api.selectedRepositoryId(),
        path: this.selected(),
        kind: this.activeKind(),
        fingerprint: this.selectedFindingFingerprint(),
        locationIndex: this.selectedLocationIndex(),
      };
      this.pendingPosition = position;
      if (this.urlSyncTimer !== null) return;
      this.urlSyncTimer = setTimeout(() => {
        this.urlSyncTimer = null;
        const pending = this.pendingPosition;
        this.pendingPosition = null;
        if (pending) this.syncUrl(pending);
      }, URL_SYNC_DEBOUNCE_MS);
    });
    effect(() => {
      const file = this.api.file();
      const fingerprint = this.selectedFindingFingerprint();
      const kind = this.activeKind();
      if (!file || !fingerprint) return;
      const restored = file.metaDocuments.find(meta => meta.kind === kind)?.findings.find(candidate =>
        candidate.fingerprint === fingerprint);
      if (restored && this.selectedFinding()?.fingerprint !== fingerprint) this.selectedFinding.set(restored);
    });
    // A hosted API answers 401 without a token. Ask for one instead of failing quietly.
    effect(() => {
      if (this.access.unauthorizedAt() === 0) return;
      this.apiAccessRejected.set(true);
      this.apiAccessDialogOpen.set(true);
    });
    void this.initialize();
    this.quotaRefreshTimer = setInterval(() => void this.api.loadQuotas(), 60_000);
  }

  private async initialize(): Promise<void> {
    const preferredRepository = new URLSearchParams(location.search).get('repo') ||
      localStorage.getItem(LAST_REPOSITORY_STORAGE_KEY);
    const preferredPath = this.selected();
    await this.api.loadRepositories(preferredRepository);
    if (this.api.connectionState() === 'offline') return;
    localStorage.setItem(LAST_REPOSITORY_STORAGE_KEY, this.api.selectedRepositoryId());
    await this.api.loadModelCatalog();
    const dashboardLoading = this.api.loadProjectDashboard();
    await this.api.loadTree();
    if (preferredPath !== '.' && !this.api.nodeAt(preferredPath)) await this.api.searchTree(preferredPath);
    void dashboardLoading;
    await this.api.loadReviewRuns();
    await Promise.all([this.api.loadUsage(), this.api.loadQuotas()]);
    if (!this.api.quotas().providers.length) setTimeout(() => void this.api.loadQuotas(), 2_000);
    const path = this.selectionPathOrFirst(this.selected());
    if (path) this.open(path, false, false, !!this.selectedFindingFingerprint());
  }

  ngOnDestroy(): void {
    clearInterval(this.quotaRefreshTimer);
    if (this.urlSyncTimer !== null) clearTimeout(this.urlSyncTimer);
  }

  /**
   * Writes one history entry per visited position and replaces it for refinements such as
   * selecting another finding in the same file. Without the push, every navigation replaced the
   * single entry and the browser's Back button left the application entirely.
   */
  private syncUrl(position: ShellPosition): void {
    const params = new URLSearchParams(location.search);
    writeFindingRoute(params, position.fingerprint, position.locationIndex);
    const href = new URL(location.href);
    href.search = params.toString();
    const push = this.historyPosition !== null
      && (this.historyPosition.repository !== position.repository || this.historyPosition.path !== position.path);
    this.historyPosition = { repository: position.repository, path: position.path };
    reportUrlPreviewNavigation({
      href: href.href,
      applyUrl: url => push ? history.pushState(null, '', url) : history.replaceState(null, '', url),
      postToParent: (message, targetOrigin) => window.parent.postMessage(message, targetOrigin),
    }, { path: position.path, kind: position.kind, repository: position.repository }, this.embedded());
  }

  /** Restores the shell position a Back or Forward navigation moved to. */
  onPopState(): void {
    const params = new URLSearchParams(location.search);
    const route = readFindingRoute(location.search);
    const kind = params.get('kind') as ReviewKind | null;
    const repository = params.get('repo');
    const path = params.get('path') || '.';
    this.selectedFindingFingerprint.set(route.fingerprint);
    this.selectedLocationIndex.set(route.locationIndex);
    if (kind && this.reviewKinds.includes(kind)) this.activeKind.set(kind);
    // The popped entry already exists, so the next sync must replace it rather than push again.
    this.historyPosition = { repository: repository ?? this.api.selectedRepositoryId(), path };
    if (repository && repository !== this.api.selectedRepositoryId()) {
      void this.switchRepository(repository);
      return;
    }
    this.open(path, false, true, true);
  }

  quotaRemaining(provider: QuotaProvider): number | null {
    const values = provider.windows.map(window => window.remainingPct).filter((value): value is number => value !== null);
    return values.length ? Math.min(...values) : null;
  }

  quotaRemainingLabel(provider: QuotaProvider): string {
    const remaining = this.quotaRemaining(provider);
    return remaining === null ? 'unavailable' : `${Math.round(remaining)}%`;
  }

  quotaTooltip(provider: QuotaProvider): string {
    if (!provider.windows.length) return `${provider.provider}: ${provider.error || 'quota unavailable'}`;
    const plan = provider.plan ? ` (${provider.plan})` : '';
    return `${provider.provider}${plan}\n${provider.windows.map(window => {
      const reset = window.resetLabel || (window.resetAt ? `resets ${new Date(window.resetAt).toLocaleString()}` : '');
      return `${window.label}: ${window.remainingPct === null ? 'unavailable' : Math.round(window.remainingPct) + '% remaining'}${reset ? ` · ${reset}` : ''}`;
    }).join('\n')}`;
  }

  private openSequence = 0;

  async open(path: string, track = true, expandContainer = false, preserveFinding = false): Promise<void> {
    const start = performance.now();
    const sequence = ++this.openSequence;
    const repositoryId = this.api.selectedRepositoryId();
    const isCurrent = () => sequence === this.openSequence
      && this.selected() === path && this.api.selectedRepositoryId() === repositoryId;
    this.selected.set(path);
    this.api.clearFile();
    if (!preserveFinding) this.clearFindingSelection();
    let node = this.api.nodeAt(path);
    // Dashboard links can point below the levels loaded by the lazy explorer. An absent
    // node is unresolved, not a container: fetch its identity before choosing a view.
    if (!node && path !== '.') {
      this.api.loading.set(true);
      node = await this.api.resolveNode(path, repositoryId);
      if (!isCurrent()) return;
    }
    if (path === '.' || (node && node.level !== 'file')) {
      if (node && path !== '.' && node.level !== 'project') {
        await this.api.loadTreeChildren(node, repositoryId);
        if (!isCurrent()) return;
        node = this.api.nodeAt(path);
      }
      this.api.loading.set(false);
      if (expandContainer) this.explorer()?.expandPath(path);
      console.info(JSON.stringify({ event: 'qs.container.opened', path, level: node?.level ?? 'project', childCount: node?.children.length ?? 0 }));
      return;
    }
    // A stale dashboard path or a failed lookup still gets a file response, including
    // the editor's actionable not-found/access/error state instead of an empty pane.
    await this.api.loadFile(path);
    if (!isCurrent()) return;
    const kinds = this.api.file()?.metaDocuments.map(meta => meta.kind) ?? [];
    if (!kinds.includes(this.activeKind())) this.activeKind.set(kinds[0] ?? 'code');
    if (track) requestAnimationFrame(() => this.measure('qs.file.first-content', start, 150));
  }

  selectKind(kind: ReviewKind): void {
    const start = performance.now();
    this.activeKind.set(kind);
    this.clearFindingSelection();
    requestAnimationFrame(() => this.measure('qs.review.aspect-switch', start, 50));
  }

  selectFinding(finding: ReviewFinding): void {
    this.selectedFinding.set(finding);
    this.selectedFindingFingerprint.set(finding.fingerprint ?? null);
    const location = finding.locations.findIndex(candidate => candidate.path === this.selected());
    this.selectedLocationIndex.set(Math.max(0, location));
  }

  async openFindingLocation(event: { finding: ReviewFinding; locationIndex: number }): Promise<void> {
    const location = event.finding.locations[event.locationIndex];
    if (!location?.range) return;
    this.selectedFinding.set(event.finding);
    this.selectedFindingFingerprint.set(event.finding.fingerprint ?? null);
    this.selectedLocationIndex.set(event.locationIndex);
    if (location.path !== this.selected()) {
      this.selected.set(location.path);
      await this.api.loadFile(location.path);
      const restored = this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind())?.findings
        .find(candidate => candidate.fingerprint === this.selectedFindingFingerprint());
      this.selectedFinding.set(restored ?? event.finding);
    }
  }

  focusReviewLauncher(): void { this.reviewFocusRequest.update(value => value + 1); }

  private clearFindingSelection(): void {
    this.selectedFinding.set(null);
    this.selectedFindingFingerprint.set(null);
    this.selectedLocationIndex.set(0);
  }

  openGuidelines(): void { this.guidelines.openGuidelines(); }
  newGuideline(): void { this.guidelines.newGuideline(); }
  editGuideline(value: Guideline): void { this.guidelines.editGuideline(value); }
  saveGuideline(): Promise<void> { return this.guidelines.saveGuideline(); }
  deleteGuideline(): void { this.guidelines.deleteGuideline(); }
  installGuideline(id: string): Promise<void> { return this.guidelines.installGuideline(id); }
  dryRunGuideline(): Promise<void> { return this.guidelines.dryRunGuideline(this.activeKind()); }
  editRepository(value: RepositoryRegistration): void { this.repositories.editRepository(value); }
  toggleReviewKind(kind: ReviewKind, enabled: boolean): void { this.repositories.toggleReviewKind(kind, enabled); }
  setDefaultTokenCap(value: string): void { this.repositories.setDefaultTokenCap(value); }
  normalizeDefaultTokenCap(): void { this.repositories.normalizeDefaultTokenCap(); }
  setDefaultCostCap(value: number | null): void { this.repositories.setDefaultCostCap(value); }
  saveRepository(): Promise<void> { return this.repositories.saveRepository(id => this.switchRepository(id)); }

  runConfirmation(): Promise<void> { return this.confirmations.run(); }

  openTrace(path: string): void { this.guidelineDialogOpen.set(false); this.open(path); }

  async switchRepository(id: string): Promise<void> {
    if (id === this.api.selectedRepositoryId()) {
      this.repositoryMenuOpen.set(false);
      return;
    }
    const started = performance.now();
    this.repositoryMenuOpen.set(false);
    localStorage.setItem(LAST_REPOSITORY_STORAGE_KEY, id);
    this.selected.set('.');
    this.selectedFinding.set(null);
    const switching = this.api.selectRepository(id);
    requestAnimationFrame(() => this.measure('qs.repository.transition-visible', started, 100));
    await switching;
    requestAnimationFrame(() => this.measure('qs.repository.switch.usable', started, 500));
    const path = this.selectionPathOrFirst('');
    if (path) this.open(path, false);
  }

  async openAttackCoverage(): Promise<void> {
    this.attackCoverageDialogOpen.set(true);
  }

  onboardRepository(): void {
    this.repositoryMenuOpen.set(false);
    this.editingRepositoryId.set(null);
    this.repositories.reset();
    this.repositoryTokenCapText = formatTokenCount(this.repositoryForm.defaultReviewTokenCap);
    this.repositoryError.set('');
    this.repositoryTokenCapError.set('');
    this.repositoryDialogOpen.set(true);
  }

  manageRepositories(): void {
    this.repositoryMenuOpen.set(false);
    const repository = this.api.selectedRepository() ?? this.api.repositories()[0];
    if (repository) this.editRepository(repository);
    this.repositoryDialogOpen.set(true);
  }

  openAgentStudioImport(): void {
    this.repositoryMenuOpen.set(false);
    this.agentStudioImportDialogOpen.set(true);
    void this.runAgentStudioImport();
  }

  closeAgentStudioImportDialog(): void {
    this.agentStudioImportDialogOpen.set(false);
  }

  async runAgentStudioImport(): Promise<void> {
    this.agentStudioImporting.set(true);
    this.agentStudioImportError.set('');
    this.agentStudioImportResult.set(null);
    try {
      const result = await this.api.importFromAgentStudio();
      this.agentStudioImportResult.set(result);
    } catch (error) {
      this.agentStudioImportError.set(this.api.errorMessage(error));
    } finally {
      this.agentStudioImporting.set(false);
    }
  }

  archiveRepository(repository: RepositoryRegistration): void {
    this.confirmation.set({
      eyebrow: 'Repository registry',
      heading: `Archive ${repository.displayName}?`,
      message: 'The repository leaves the switcher and its reviews stop being tracked here. No file in the repository is changed.',
      confirmLabel: 'Archive',
      danger: true,
      confirm: () => this.confirmArchiveRepository(repository),
    });
  }

  /** Runs the pending confirmation, then closes it whatever the outcome. */
  private async confirmArchiveRepository(repository: RepositoryRegistration): Promise<void> {
    const wasSelected = repository.id === this.api.selectedRepositoryId();
    try {
      await this.api.archiveRepository(repository.id);
      if (wasSelected) {
        await this.api.selectRepository(this.api.selectedRepositoryId());
        localStorage.setItem(LAST_REPOSITORY_STORAGE_KEY, this.api.selectedRepositoryId());
        const path = this.selectionPathOrFirst('');
        if (path) this.open(path, false);
        this.repositoryDialogOpen.set(false);
      } else if (this.api.repositories().length) {
        this.editRepository(this.api.repositories()[0]);
      }
    } catch (error) {
      this.repositoryError.set(this.api.errorMessage(error));
    }
  }

  onResize(): void { this.viewportHeight.set(window.innerHeight); }

  setTheme(): void {
    const next = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(next);
    localStorage.setItem('qs-theme', next);
  }

  openApiAccess(): void {
    this.repositoryMenuOpen.set(false);
    this.apiAccessRejected.set(false);
    this.apiAccessDialogOpen.set(true);
  }

  closeApiAccess(): void {
    this.apiAccessDialogOpen.set(false);
    this.apiAccessRejected.set(false);
  }

  openUsageHistory(): void {
    this.usageHistoryOpen.set(true);
    void this.api.loadUsage();
  }

  closeUsageHistory(): void {
    this.usageHistoryOpen.set(false);
    queueMicrotask(() => this.usageButton().nativeElement.focus());
  }

  toggleExplorer(): void { this.layout.toggleExplorer(); }

  toggleReview(): void { this.layout.toggleReview(); }

  resetExplorerWidth(): void { this.layout.resetExplorerWidth(); }

  resetReviewWidth(): void { this.layout.resetReviewWidth(); }

  onKeydown(event: KeyboardEvent): void { this.layout.onKeydown(event); }

  startExplorerDrag(event: PointerEvent): void { this.layout.startExplorerDrag(event); }

  startReviewDrag(event: PointerEvent): void { this.layout.startReviewDrag(event); }

  onDragMove(event: PointerEvent): void { this.layout.onDragMove(event); }

  onDragEnd(): void { this.layout.onDragEnd(); }

  onHandleKeydown(event: KeyboardEvent, pane: ResizablePane): void { this.layout.onHandleKeydown(event, pane); }

  private measure(name: string, start: number, budget: number): void {
    const duration = performance.now() - start;
    performance.measure(name, { start, end: performance.now(), detail: { budget, path: this.selected() } });
    console.info(JSON.stringify({ event: name, durationMs: +duration.toFixed(2), budgetMs: budget, withinBudget: duration < budget }));
  }

  private selectionPathOrFirst(preferred: string): string {
    if (!preferred || preferred === '.') return '.';
    return this.api.nodeAt(preferred)?.path ?? '.';
  }

  private detectEmbedded(): boolean {
    if (typeof window === 'undefined' || typeof document === 'undefined') return false;
    try {
      return window.self !== window.top;
    } catch {
      return true;
    }
  }
}
