import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, effect, inject, signal, viewChild } from '@angular/core';
import { ApiAccess } from './api-access';
import { AgentStudioImport } from './agent-studio-import/agent-studio-import';
import { ApiAccessDialog } from './api-access-dialog/api-access-dialog';
import { ConfirmDialog } from './dialog/confirm-dialog';
import { GuidelineDialog } from './guideline-dialog/guideline-dialog';
import { GuidelineForm } from './guideline-dialog/guideline-form';
import { AttackCoverage } from './attack-coverage/attack-coverage';
import { Editor } from './editor/editor';
import { Explorer } from './explorer/explorer';
import { QualityApi } from './quality-api';
import { AgentStudioImportResponse, Guideline, GuidelineDraft, GuidelineImpact, QuotaProvider, RepositoryRegistration, RepositoryRegistrationRequest, ReviewFinding, ReviewKind } from './contracts';
import { ReviewPanel } from './review-panel/review-panel';
import { ReviewActions } from './review-actions/review-actions';
import { ProjectDashboardView } from './project-dashboard/project-dashboard';
import { UsageHistory } from './usage-history/usage-history';
import { readFindingRoute, writeFindingRoute } from './review-navigation';
import { reportUrlPreviewNavigation } from './url-preview-embed';
import { formatTokenCount, parseTokenCount } from './format';
import { RepositoryDialog } from './repository-dialog/repository-dialog';

const LAYOUT_STORAGE_KEY = 'qs-layout';
const LAST_REPOSITORY_STORAGE_KEY = 'qs-last-repository';
/** Collapses a salvo of position changes into one history write. */
const URL_SYNC_DEBOUNCE_MS = 120;
const RESIZE_HANDLE_WIDTH = 6;
const EXPLORER_DEFAULT_WIDTH = 280;
const EXPLORER_MIN_WIDTH = 180;
const EXPLORER_MAX_WIDTH = 560;
const REVIEW_DEFAULT_WIDTH = 320;
const REVIEW_MIN_WIDTH = 240;
const REVIEW_MAX_WIDTH = 640;

interface WorkspaceLayout {
  explorerVisible: boolean;
  reviewVisible: boolean;
  explorerWidth: number;
  reviewWidth: number;
}

type ResizablePane = 'explorer' | 'review';
interface ShellPosition {
  repository: string;
  path: string;
  kind: ReviewKind;
  fingerprint: string | null;
  locationIndex: number;
}
interface PendingConfirmation {
  eyebrow: string;
  heading: string;
  message: string;
  confirmLabel: string;
  danger: boolean;
  confirm: () => void | Promise<void>;
}

@Component({
  selector: 'app-root',
  imports: [Explorer, Editor, ReviewPanel, ReviewActions, AttackCoverage, UsageHistory, ProjectDashboardView, RepositoryDialog, ApiAccessDialog, ConfirmDialog, GuidelineDialog, AgentStudioImport],
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
  readonly repositoryDialogOpen = signal(false);
  readonly editingRepositoryId = signal<string | null>(null);
  readonly repositoryError = signal('');
  readonly repositoryTokenCapError = signal('');
  readonly repositorySaving = signal(false);
  readonly agentStudioImportDialogOpen = signal(false);
  readonly agentStudioImporting = signal(false);
  readonly agentStudioImportResult = signal<AgentStudioImportResponse | null>(null);
  readonly agentStudioImportError = signal('');
  readonly guidelineDialogOpen = signal(false);
  readonly editingGuidelineId = signal<string | null>(null);
  readonly guidelineError = signal('');
  readonly guidelineSaving = signal(false);
  readonly guidelineDryRunning = signal(false);
  readonly guidelineImpact = signal<GuidelineImpact | null>(null);
  readonly attackCoverageDialogOpen = signal(false);
  readonly usageHistoryOpen = signal(false);
  readonly apiAccessDialogOpen = signal(false);
  readonly apiAccessRejected = signal(false);
  readonly confirmation = signal<PendingConfirmation | null>(null);
  readonly viewportHeight = signal(typeof window === 'undefined' ? 1000 : window.innerHeight);
  readonly selectedNode = computed(() => this.api.nodeAt(this.selected())
    ?? (this.selected() === '.' ? this.api.allNodes().find(node => node.level === 'project') : undefined));
  readonly explorerSelectedPath = computed(() => this.selected() === '.' ? this.selectedNode()?.path ?? '.' : this.selected());
  readonly isProjectView = computed(() => this.selected() === '.' || this.selectedNode()?.level === 'project');
  readonly editingRepository = computed(() => this.api.repositories().find(repository => repository.id === this.editingRepositoryId()) ?? null);
  readonly usageTotalLabel = computed(() => new Intl.NumberFormat('en-US').format(
    this.api.usage().inputTokens + this.api.usage().outputTokens));
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  repositoryForm: RepositoryRegistrationRequest = this.emptyRepositoryForm();
  repositoryTokenCapText = formatTokenCount(this.repositoryForm.defaultReviewTokenCap);
  guidelineForm: GuidelineForm = this.emptyGuidelineForm();

  // Panel visibility/width and drag state. Persisted layout is loaded once here so the
  // initial signal values already reflect it (no flash of the default layout on load).
  private readonly initialLayout = this.loadLayout();
  readonly explorerVisible = signal(this.initialLayout.explorerVisible);
  readonly reviewVisible = signal(this.initialLayout.reviewVisible);
  readonly explorerWidth = signal(this.initialLayout.explorerWidth);
  readonly reviewWidth = signal(this.initialLayout.reviewWidth);
  readonly dragging = signal<ResizablePane | null>(null);
  readonly gridTemplateColumns = computed(() => {
    const explorerTrack = this.explorerVisible() ? `${this.explorerWidth()}px ${RESIZE_HANDLE_WIDTH}px` : '0px 0px';
    const reviewTrack = this.reviewVisible() ? `${RESIZE_HANDLE_WIDTH}px ${this.reviewWidth()}px` : '0px 0px';
    return `${explorerTrack} minmax(400px,1fr) ${reviewTrack}`;
  });
  private urlSyncTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingPosition: ShellPosition | null = null;
  /** The position the current history entry represents, so refinements only replace it. */
  private historyPosition: { repository: string; path: string } | null = null;
  private dragStartX = 0;
  private dragStartWidth = 0;
  private dragFrame: number | null = null;
  private pendingClientX = 0;
  private readonly quotaRefreshTimer: ReturnType<typeof setInterval>;

  constructor() {
    effect(() => document.documentElement.dataset['theme'] = this.theme());
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
    // Persist collapse/resize layout under its own key, independent of qs-theme.
    effect(() => {
      const layout: WorkspaceLayout = {
        explorerVisible: this.explorerVisible(),
        reviewVisible: this.reviewVisible(),
        explorerWidth: this.explorerWidth(),
        reviewWidth: this.reviewWidth(),
      };
      localStorage.setItem(LAYOUT_STORAGE_KEY, JSON.stringify(layout));
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

  open(path: string, track = true, expandContainer = false, preserveFinding = false): void {
    const start = performance.now();
    this.selected.set(path);
    const node = this.api.nodeAt(path);
    if (node?.level !== 'file') {
      this.api.clearFile();
      if (!preserveFinding) this.clearFindingSelection();
      if (expandContainer) this.explorer()?.expandPath(path);
      console.info(JSON.stringify({ event: 'qs.container.opened', path, level: node?.level ?? 'unknown', childCount: node?.children.length ?? 0 }));
      return;
    }
    this.api.loadFile(path).then(() => {
      const kinds = this.api.file()?.metaDocuments.map(meta => meta.kind) ?? [];
      if (!kinds.includes(this.activeKind())) this.activeKind.set(kinds[0] ?? 'code');
      if (!preserveFinding) this.clearFindingSelection();
      if (track) requestAnimationFrame(() => this.measure('qs.file.first-content', start, 150));
    });
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

  openGuidelines(): void {
    this.guidelineDialogOpen.set(true);
    const first = this.api.guidelines()[0];
    if (first) this.editGuideline(first); else this.newGuideline();
  }

  newGuideline(): void {
    this.editingGuidelineId.set(null);
    this.guidelineForm = this.emptyGuidelineForm();
    this.guidelineError.set('');
    this.guidelineImpact.set(null);
  }

  editGuideline(guideline: Guideline): void {
    this.editingGuidelineId.set(guideline.id);
    this.guidelineForm = { id: guideline.id, enabled: guideline.enabled, priority: guideline.priority,
      kinds: guideline.kinds.join(', '), levels: guideline.levels.join(', '), content: guideline.content };
    this.guidelineError.set('');
    this.guidelineImpact.set(null);
  }

  async saveGuideline(): Promise<void> {
    this.guidelineSaving.set(true); this.guidelineError.set('');
    try {
      const draft = this.guidelineDraft();
      const existing = this.editingGuidelineId();
      const saved = existing ? await this.api.updateGuideline(existing, draft) : await this.api.createGuideline(draft);
      this.editGuideline(saved);
    } catch (error) { this.guidelineError.set(this.api.errorMessage(error)); }
    finally { this.guidelineSaving.set(false); }
  }

  deleteGuideline(): void {
    const id = this.editingGuidelineId();
    if (!id) return;
    this.confirmation.set({
      eyebrow: 'Repository policy',
      heading: `Delete guideline ${id}?`,
      message: 'The guideline file is removed from the repository. Reviews started afterwards no longer apply it.',
      confirmLabel: 'Delete file',
      danger: true,
      confirm: () => this.confirmDeleteGuideline(id),
    });
  }

  private async confirmDeleteGuideline(id: string): Promise<void> {
    try {
      await this.api.deleteGuideline(id);
      if (this.api.guidelines().length) this.editGuideline(this.api.guidelines()[0]);
      else this.newGuideline();
    } catch (error) {
      this.guidelineError.set(this.api.errorMessage(error));
    }
  }

  async installGuideline(id: string): Promise<void> {
    try { this.editGuideline(await this.api.installGuideline(id)); }
    catch (error) { this.guidelineError.set(this.api.errorMessage(error)); }
  }

  async dryRunGuideline(): Promise<void> {
    const sample = this.api.file()?.path ?? this.api.allNodes().find(node => node.level === 'file')?.path;
    if (!sample) { this.guidelineError.set('Open or select a sample file first.'); return; }
    this.guidelineDryRunning.set(true); this.guidelineError.set(''); this.guidelineImpact.set(null);
    const requestedKind = this.guidelineDraft().kinds.find(kind => ['code', 'security', 'performance'].includes(kind)) as ReviewKind | undefined;
    try { this.guidelineImpact.set(await this.api.guidelineImpact(this.guidelineDraft(), [sample], requestedKind ?? this.activeKind())); }
    catch (error) { this.guidelineError.set(this.api.errorMessage(error)); }
    finally { this.guidelineDryRunning.set(false); }
  }

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
    this.repositoryForm = this.emptyRepositoryForm();
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

  editRepository(repository: RepositoryRegistration): void {
    this.editingRepositoryId.set(repository.id);
    this.repositoryForm = {
      id: repository.id,
      displayName: repository.displayName,
      rootPath: repository.rootPath,
      globalInputsDirectory: repository.globalInputsDirectory,
      inputBudgetCharacters: repository.inputBudgetCharacters,
      enabledReviewKinds: [...repository.enabledReviewKinds],
      defaultReviewTokenCap: repository.defaultReviewTokenCap,
      defaultReviewCostCap: repository.defaultReviewCostCap,
    };
    this.repositoryTokenCapText = formatTokenCount(repository.defaultReviewTokenCap);
    this.repositoryError.set('');
    this.repositoryTokenCapError.set('');
  }

  toggleReviewKind(kind: ReviewKind, enabled: boolean): void {
    this.repositoryForm.enabledReviewKinds = enabled
      ? [...new Set([...this.repositoryForm.enabledReviewKinds, kind])]
      : this.repositoryForm.enabledReviewKinds.filter(existing => existing !== kind);
  }

  setDefaultTokenCap(value: string): void {
    this.repositoryTokenCapText = value;
    if (!value.trim()) {
      this.repositoryForm.defaultReviewTokenCap = null;
      this.repositoryTokenCapError.set('');
      return;
    }
    const parsed = parseTokenCount(value);
    if (parsed === null || parsed > 1_000_000_000) {
      this.repositoryForm.defaultReviewTokenCap = null;
      this.repositoryTokenCapError.set('Enter 1 to 1B tokens, for example 100k or 0.1M.');
      return;
    }
    this.repositoryForm.defaultReviewTokenCap = parsed;
    this.repositoryTokenCapError.set('');
    this.repositoryForm.defaultReviewCostCap = null;
  }

  normalizeDefaultTokenCap(): void {
    if (this.repositoryForm.defaultReviewTokenCap !== null) {
      this.repositoryTokenCapText = formatTokenCount(this.repositoryForm.defaultReviewTokenCap);
    }
  }

  setDefaultCostCap(value: number | null): void {
    this.repositoryForm.defaultReviewCostCap = value;
    if (value !== null) {
      this.repositoryForm.defaultReviewTokenCap = null;
      this.repositoryTokenCapText = '';
      this.repositoryTokenCapError.set('');
    }
  }

  async saveRepository(): Promise<void> {
    this.repositorySaving.set(true);
    this.repositoryError.set('');
    try {
      const editingId = this.editingRepositoryId();
      const saved = editingId
        ? await this.api.updateRepository(editingId, this.repositoryForm)
        : await this.api.createRepository(this.repositoryForm);
      if (!editingId) await this.switchRepository(saved.id);
      this.repositoryDialogOpen.set(false);
    } catch (error) {
      this.repositoryError.set(this.api.errorMessage(error));
    } finally {
      this.repositorySaving.set(false);
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
  async runConfirmation(): Promise<void> {
    const pending = this.confirmation();
    this.confirmation.set(null);
    await pending?.confirm();
  }

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

  toggleExplorer(): void { this.explorerVisible.update(visible => !visible); }

  toggleReview(): void { this.reviewVisible.update(visible => !visible); }

  resetExplorerWidth(): void { this.explorerWidth.set(EXPLORER_DEFAULT_WIDTH); }

  resetReviewWidth(): void { this.reviewWidth.set(REVIEW_DEFAULT_WIDTH); }

  // Ctrl+B toggles the Explorer, Ctrl+Alt+B toggles the Review panel.
  onKeydown(event: KeyboardEvent): void {
    if (!event.ctrlKey || event.key.toLowerCase() !== 'b') return;
    event.preventDefault();
    if (event.altKey) this.toggleReview(); else this.toggleExplorer();
  }

  startExplorerDrag(event: PointerEvent): void { this.beginDrag('explorer', event); }

  startReviewDrag(event: PointerEvent): void { this.beginDrag('review', event); }

  onDragMove(event: PointerEvent): void {
    if (!this.dragging()) return;
    // Coalesce rapid pointermove events to one grid-column update per frame.
    this.pendingClientX = event.clientX;
    if (this.dragFrame !== null) return;
    this.dragFrame = requestAnimationFrame(() => {
      this.dragFrame = null;
      this.applyDrag();
    });
  }

  onDragEnd(): void {
    if (this.dragFrame !== null) {
      cancelAnimationFrame(this.dragFrame);
      this.dragFrame = null;
    }
    this.dragging.set(null);
  }

  onHandleKeydown(event: KeyboardEvent, pane: ResizablePane): void {
    const step = 10;
    if (event.key === 'ArrowLeft') { this.nudgeWidth(pane, pane === 'explorer' ? -step : step); event.preventDefault(); }
    else if (event.key === 'ArrowRight') { this.nudgeWidth(pane, pane === 'explorer' ? step : -step); event.preventDefault(); }
    else if (event.key === 'Home' || event.key === 'Enter') {
      if (pane === 'explorer') this.resetExplorerWidth(); else this.resetReviewWidth();
      event.preventDefault();
    }
  }

  private beginDrag(pane: ResizablePane, event: PointerEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    this.dragging.set(pane);
    this.dragStartX = event.clientX;
    this.dragStartWidth = pane === 'explorer' ? this.explorerWidth() : this.reviewWidth();
    (event.target as HTMLElement).setPointerCapture(event.pointerId);
  }

  private applyDrag(): void {
    const pane = this.dragging();
    if (!pane) return;
    const delta = this.pendingClientX - this.dragStartX;
    if (pane === 'explorer') {
      this.explorerWidth.set(this.clampWidth(this.dragStartWidth + delta, EXPLORER_MIN_WIDTH, EXPLORER_MAX_WIDTH, EXPLORER_DEFAULT_WIDTH));
    } else {
      this.reviewWidth.set(this.clampWidth(this.dragStartWidth - delta, REVIEW_MIN_WIDTH, REVIEW_MAX_WIDTH, REVIEW_DEFAULT_WIDTH));
    }
  }

  private nudgeWidth(pane: ResizablePane, delta: number): void {
    if (pane === 'explorer') this.explorerWidth.set(this.clampWidth(this.explorerWidth() + delta, EXPLORER_MIN_WIDTH, EXPLORER_MAX_WIDTH, EXPLORER_DEFAULT_WIDTH));
    else this.reviewWidth.set(this.clampWidth(this.reviewWidth() + delta, REVIEW_MIN_WIDTH, REVIEW_MAX_WIDTH, REVIEW_DEFAULT_WIDTH));
  }

  private loadLayout(): WorkspaceLayout {
    const defaults: WorkspaceLayout = { explorerVisible: true, reviewVisible: true, explorerWidth: EXPLORER_DEFAULT_WIDTH, reviewWidth: REVIEW_DEFAULT_WIDTH };
    try {
      const raw = localStorage.getItem(LAYOUT_STORAGE_KEY);
      if (!raw) return defaults;
      const parsed = JSON.parse(raw);
      return {
        explorerVisible: typeof parsed.explorerVisible === 'boolean' ? parsed.explorerVisible : defaults.explorerVisible,
        reviewVisible: typeof parsed.reviewVisible === 'boolean' ? parsed.reviewVisible : defaults.reviewVisible,
        explorerWidth: this.clampWidth(parsed.explorerWidth, EXPLORER_MIN_WIDTH, EXPLORER_MAX_WIDTH, defaults.explorerWidth),
        reviewWidth: this.clampWidth(parsed.reviewWidth, REVIEW_MIN_WIDTH, REVIEW_MAX_WIDTH, defaults.reviewWidth),
      };
    } catch {
      return defaults;
    }
  }

  private clampWidth(value: unknown, min: number, max: number, fallback: number): number {
    return typeof value === 'number' && Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback;
  }

  private measure(name: string, start: number, budget: number): void {
    const duration = performance.now() - start;
    performance.measure(name, { start, end: performance.now(), detail: { budget, path: this.selected() } });
    console.info(JSON.stringify({ event: name, durationMs: +duration.toFixed(2), budgetMs: budget, withinBudget: duration < budget }));
  }

  private selectionPathOrFirst(preferred: string): string {
    if (!preferred || preferred === '.') return '.';
    return this.api.nodeAt(preferred)?.path ?? '.';
  }

  private emptyRepositoryForm(): RepositoryRegistrationRequest {
    return { displayName: '', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'], defaultReviewTokenCap: 100000, defaultReviewCostCap: null };
  }

  private emptyGuidelineForm(): GuidelineForm {
    return { id: '', enabled: true, priority: 50, kinds: 'code', levels: 'file', content: '' };
  }

  private guidelineDraft(): GuidelineDraft {
    const values = (value: string) => value.split(',').map(item => item.trim().toLowerCase()).filter(Boolean);
    return { id: this.guidelineForm.id.trim(), enabled: this.guidelineForm.enabled,
      priority: Number(this.guidelineForm.priority), kinds: values(this.guidelineForm.kinds),
      levels: values(this.guidelineForm.levels), content: this.guidelineForm.content };
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
