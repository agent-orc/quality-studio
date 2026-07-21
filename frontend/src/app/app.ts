import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Editor } from './editor/editor';
import { Explorer } from './explorer/explorer';
import { QualityApi, RepositoryRegistration, RepositoryRegistrationRequest, ReviewFinding, ReviewKind } from './quality-api';
import { ReviewPanel } from './review-panel/review-panel';
import { flattenTree } from './tree-utils';

const LAYOUT_STORAGE_KEY = 'qs-layout';
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

@Component({
  selector: 'app-root',
  imports: [FormsModule, Explorer, Editor, ReviewPanel],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(window:resize)': 'onResize()',
    '(window:keydown)': 'onKeydown($event)',
    '(window:pointermove)': 'onDragMove($event)',
    '(window:pointerup)': 'onDragEnd()',
    '(window:pointercancel)': 'onDragEnd()',
  },
})
export class App {
  readonly api = inject(QualityApi);
  readonly explorer = viewChild(Explorer);
  readonly embedded = signal(this.detectEmbedded());
  readonly theme = signal<'dark' | 'light'>((new URLSearchParams(location.search).get('theme') as 'dark' | 'light') || (localStorage.getItem('qs-theme') as 'dark' | 'light') || 'dark');
  readonly selected = signal(new URLSearchParams(location.search).get('path') || 'src/QualityStudio.Api/Program.cs');
  readonly activeKind = signal<ReviewKind>((new URLSearchParams(location.search).get('kind') as ReviewKind) || 'code');
  readonly selectedFinding = signal<ReviewFinding | null>(null);
  readonly repositoryMenuOpen = signal(false);
  readonly repositoryDialogOpen = signal(false);
  readonly editingRepositoryId = signal<string | null>(null);
  readonly repositoryError = signal('');
  readonly repositorySaving = signal(false);
  readonly viewportHeight = signal(typeof window === 'undefined' ? 1000 : window.innerHeight);
  readonly selectedNode = computed(() => flattenTree(this.api.tree(), new Set(), true).find(n => n.path === this.selected()));
  readonly editingRepository = computed(() => this.api.repositories().find(repository => repository.id === this.editingRepositoryId()) ?? null);
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  repositoryForm: RepositoryRegistrationRequest = this.emptyRepositoryForm();

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
  private dragStartX = 0;
  private dragStartWidth = 0;
  private dragFrame: number | null = null;
  private pendingClientX = 0;

  constructor() {
    effect(() => document.documentElement.dataset['theme'] = this.theme());
    // Deep-linkable position: mirror the selected path and review kind into the
    // URL, and report every navigation to an embedding Studio preview so its
    // address bar stays current (url-preview-embed contract).
    effect(() => {
      const params = new URLSearchParams(location.search);
      params.set('path', this.selected());
      params.set('kind', this.activeKind());
      params.set('repo', this.api.selectedRepositoryId());
      history.replaceState(null, '', `?${params}`);
      if (this.embedded()) {
        window.parent.postMessage({ source: 'url-preview-embed', type: 'navigation', url: location.href }, '*');
      }
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
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    const preferredRepository = new URLSearchParams(location.search).get('repo');
    await this.api.loadRepositories(preferredRepository);
    await this.api.loadTree();
    await this.api.loadReviewRuns();
    const path = this.selectionPathOrFirst(this.selected());
    if (path) this.open(path, false);
  }

  open(path: string, track = true, expandContainer = false): void {
    const start = performance.now();
    this.selected.set(path);
    const node = flattenTree(this.api.tree(), new Set(), true).find(candidate => candidate.path === path);
    if (node?.level !== 'file') {
      this.api.clearFile();
      this.selectedFinding.set(null);
      if (expandContainer) this.explorer()?.expandPath(path);
      console.info(JSON.stringify({ event: 'qs.container.opened', path, level: node?.level ?? 'unknown', childCount: node?.children.length ?? 0 }));
      return;
    }
    this.api.loadFile(path).then(() => {
      const kinds = this.api.file()?.metaDocuments.map(meta => meta.kind) ?? [];
      if (!kinds.includes(this.activeKind())) this.activeKind.set(kinds[0] ?? 'code');
      this.selectedFinding.set(null);
      if (track) requestAnimationFrame(() => this.measure('qs.file.first-content', start, 150));
    });
  }

  selectKind(kind: ReviewKind): void {
    const start = performance.now();
    this.activeKind.set(kind);
    this.selectedFinding.set(null);
    requestAnimationFrame(() => this.measure('qs.review.aspect-switch', start, 50));
  }

  selectFinding(finding: ReviewFinding): void { this.selectedFinding.set(finding); }

  async switchRepository(id: string): Promise<void> {
    this.repositoryMenuOpen.set(false);
    await this.api.selectRepository(id);
    const path = this.selectionPathOrFirst('');
    if (path) this.open(path, false);
  }

  onboardRepository(): void {
    this.repositoryMenuOpen.set(false);
    this.editingRepositoryId.set(null);
    this.repositoryForm = this.emptyRepositoryForm();
    this.repositoryError.set('');
    this.repositoryDialogOpen.set(true);
  }

  manageRepositories(): void {
    this.repositoryMenuOpen.set(false);
    const repository = this.api.selectedRepository() ?? this.api.repositories()[0];
    if (repository) this.editRepository(repository);
    this.repositoryDialogOpen.set(true);
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
    };
    this.repositoryError.set('');
  }

  toggleReviewKind(kind: ReviewKind, enabled: boolean): void {
    this.repositoryForm.enabledReviewKinds = enabled
      ? [...new Set([...this.repositoryForm.enabledReviewKinds, kind])]
      : this.repositoryForm.enabledReviewKinds.filter(existing => existing !== kind);
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

  async archiveRepository(repository: RepositoryRegistration): Promise<void> {
    if (!confirm(`Archive ${repository.displayName}? Its files will not be changed.`)) return;
    const wasSelected = repository.id === this.api.selectedRepositoryId();
    try {
      await this.api.archiveRepository(repository.id);
      if (wasSelected) {
        await this.api.selectRepository(this.api.selectedRepositoryId());
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
    else if (event.key === 'Home' || event.key === 'Enter') { pane === 'explorer' ? this.resetExplorerWidth() : this.resetReviewWidth(); event.preventDefault(); }
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

  private selectionPathOrFirst(preferred: string): string | null {
    const nodes = flattenTree(this.api.tree(), new Set(), true);
    const preferredNode = nodes.find(node => node.path === preferred);
    return preferredNode?.path ?? nodes.find(node => node.level === 'file')?.path ?? null;
  }

  private emptyRepositoryForm(): RepositoryRegistrationRequest {
    return { displayName: '', rootPath: '', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code', 'security', 'performance'] };
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
