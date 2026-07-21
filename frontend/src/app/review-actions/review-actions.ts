import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ModelSelector } from '../model-selector/model-selector';
import { ChatModelSelection, QualityApi, ReviewKind, TreeNode } from '../quality-api';

@Component({
  selector: 'qs-review-actions',
  imports: [FormsModule, ModelSelector],
  templateUrl: './review-actions.html',
  styleUrl: './review-actions.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewActions {
  readonly api = inject(QualityApi);
  readonly node = input<TreeNode | undefined>();
  readonly activeKind = input.required<ReviewKind>();
  readonly compact = input(false);
  readonly kindSelect = output<ReviewKind>();
  readonly starting = signal(false);
  readonly fileCount = computed(() => this.countFiles(this.node()));
  readonly activeOnNode = computed(() => this.api.reviewRuns().some(run =>
    run.path === this.node()?.path && (run.state === 'queued' || run.state === 'running')));
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];

  // The picker preselects the repository's configured default; an explicit pick here
  // overrides it for this session until the repository changes again.
  private readonly cliTypeOverride = signal<string | null>(null);
  private readonly modelOverride = signal<string | null | undefined>(undefined);
  readonly effectiveCliType = computed(() =>
    this.cliTypeOverride() ?? this.api.selectedRepository()?.defaultCliType ?? 'codex');
  readonly effectiveModel = computed(() => {
    const override = this.modelOverride();
    return override !== undefined ? override : (this.api.selectedRepository()?.defaultModel ?? null);
  });

  // Catalog for the CLI the picker last asked about, which is the draft CLI while the
  // popover is open and may differ from effectiveCliType() until Done commits it.
  private readonly catalogCliType = signal<string | null>(null);
  readonly catalogModels = computed(() => this.api.modelsFor(this.catalogCliType()));

  constructor() {
    // A different repository can enable a different set of CLIs/models, so a session
    // override from the previous repository should not silently carry over.
    effect(() => {
      this.api.selectedRepositoryId();
      this.cliTypeOverride.set(null);
      this.modelOverride.set(undefined);
    });
    void this.api.loadCliOptions();
  }

  onCatalogRequested(cliType: string): void {
    this.catalogCliType.set(cliType);
    void this.api.loadModelCatalog(cliType);
  }

  onModelCommit(selection: ChatModelSelection): void {
    this.cliTypeOverride.set(selection.cliType);
    this.modelOverride.set(selection.model || null);
  }

  async start(): Promise<void> {
    const node = this.node();
    if (!node || this.starting() || this.activeOnNode()) return;
    if (node.level === 'project' && !confirm(`Start a ${this.activeKind()} review of this project? ${this.fileCount()} files will be reviewed.`)) return;
    this.starting.set(true);
    try {
      await this.api.startReview({ path: node.path, kind: this.activeKind(), model: this.effectiveModel(), cliType: this.effectiveCliType() });
    } catch {
      // QualityApi exposes the actionable problem in reviewError for every action surface.
    } finally {
      this.starting.set(false);
    }
  }

  private countFiles(node: TreeNode | undefined): number {
    if (!node) return 0;
    if (node.level === 'file') return 1;
    return node.children.reduce((sum, child) => sum + this.countFiles(child), 0);
  }
}
