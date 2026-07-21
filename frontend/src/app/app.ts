import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Editor } from './editor/editor';
import { Explorer } from './explorer/explorer';
import { QualityApi, RepositoryRegistration, RepositoryRegistrationRequest, ReviewFinding, ReviewKind } from './quality-api';
import { ReviewPanel } from './review-panel/review-panel';
import { flattenTree } from './tree-utils';

@Component({
  selector: 'app-root',
  imports: [FormsModule, Explorer, Editor, ReviewPanel],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:resize)': 'onResize()' },
})
export class App {
  readonly api = inject(QualityApi);
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
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    const preferredRepository = new URLSearchParams(location.search).get('repo');
    await this.api.loadRepositories(preferredRepository);
    await this.api.loadTree();
    const path = this.filePathOrFirst(this.selected());
    if (path) this.open(path, false);
  }

  open(path: string, track = true): void {
    const start = performance.now();
    this.selected.set(path);
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
    const path = this.filePathOrFirst('');
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
        const path = this.filePathOrFirst('');
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

  private measure(name: string, start: number, budget: number): void {
    const duration = performance.now() - start;
    performance.measure(name, { start, end: performance.now(), detail: { budget, path: this.selected() } });
    console.info(JSON.stringify({ event: name, durationMs: +duration.toFixed(2), budgetMs: budget, withinBudget: duration < budget }));
  }

  private filePathOrFirst(preferred: string): string | null {
    const nodes = flattenTree(this.api.tree(), new Set(), true);
    const preferredNode = nodes.find(node => node.path === preferred && node.level === 'file');
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
