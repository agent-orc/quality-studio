import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { QualityApi, ReviewKind, ReviewModelOption, ReviewPreflight, ReviewRun, StartReviewRequest, TreeNode } from '../quality-api';

let reviewActionsInstance = 0;

@Component({
  selector: 'qs-review-actions',
  imports: [FormsModule],
  templateUrl: './review-actions.html',
  styleUrl: './review-actions.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:pointerdown)': 'closeOnOutsidePointer($event)' },
})
export class ReviewActions {
  readonly api = inject(QualityApi);
  private readonly element = inject(ElementRef<HTMLElement>);
  readonly modelOptionsId = `review-model-options-${++reviewActionsInstance}`;
  readonly node = input<TreeNode | undefined>();
  readonly activeKind = input.required<ReviewKind>();
  readonly compact = input(false);
  readonly focusRequest = input(0);
  readonly kindSelect = output<ReviewKind>();
  readonly starting = signal(false);
  readonly showLauncher = signal(false);
  readonly preflight = signal<ReviewPreflight | null>(null);
  readonly pendingRequest = signal<StartReviewRequest | null>(null);
  readonly cliType = signal('codex');
  readonly model = signal('');
  readonly thinkingLevel = signal('');
  readonly modelQuery = signal('');
  readonly modelPickerOpen = signal(false);
  readonly activeModelIndex = signal(-1);
  readonly force = signal(false);
  readonly capKind = signal<'repository' | 'tokens' | 'cost'>('repository');
  readonly capValue = signal<number | null>(null);
  readonly cliTypes = ['codex', 'claude', 'antigravity', 'gemini'];
  readonly fileCount = computed(() => this.countFiles(this.node()));
  readonly scopeRuns = computed(() => this.api.reviewRuns().filter(run =>
    run.path === this.node()?.path && run.kind === this.activeKind()));
  readonly currentRun = computed(() => this.scopeRuns()[0] ?? null);
  readonly reviewBlockedReason = computed(() => {
    const repository = this.api.selectedRepository();
    return repository?.reviewAllowed === false
      ? repository.reviewBlockReason ?? 'Repository review is blocked by onboarding policy.'
      : null;
  });
  readonly displayRun = computed(() => this.showLauncher() ? null : this.currentRun());
  readonly activeOnNode = computed(() => ['queued', 'running', 'paused'].includes(this.currentRun()?.state ?? ''));
  readonly currentRunPath = computed(() => {
    const run = this.currentRun();
    if (!run) return '';
    if (run.aggregateState === 'running') return `${run.path} aggregate`;
    return run.files.find(file => file.state === 'running')?.path
      ?? run.files.find(file => file.state === 'queued')?.path
      ?? run.path;
  });
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  readonly modelsForCli = computed(() => this.api.modelCatalog().models.filter(candidate =>
    candidate.cliType === this.cliType() && candidate.availableForNewRuns));
  readonly filteredModels = computed(() => {
    const query = this.modelQuery().trim().toLowerCase();
    return query
      ? this.modelsForCli().filter(candidate =>
        candidate.modelId.toLowerCase().includes(query) ||
        candidate.aliases.some(alias => alias.toLowerCase().includes(query)))
      : this.modelsForCli();
  });
  readonly selectedModel = computed(() => {
    const selected = this.model().trim().toLowerCase();
    return this.api.modelCatalog().models.find(candidate =>
      candidate.modelId.toLowerCase() === selected ||
      candidate.aliases.some(alias => alias.toLowerCase() === selected)) ?? null;
  });
  readonly thinkingOptions = computed(() => {
    if (!this.model().trim()) return [];
    return this.selectedModel()?.supportedThinkingLevels ?? this.api.modelCatalog().thinkingLevels;
  });

  constructor() {
    effect(() => {
      const request = this.focusRequest();
      if (!request) return;
      queueMicrotask(() => ((this.element.nativeElement as HTMLElement)
        .querySelector('.review-intent, .active-run-actions button') as HTMLButtonElement | null)?.focus());
    });
  }

  selectCli(value: string): void {
    this.clearPreflight();
    this.cliType.set(value);
    this.model.set('');
    this.thinkingLevel.set('');
    this.modelQuery.set('');
    this.modelPickerOpen.set(false);
  }

  openModelPicker(): void {
    this.modelQuery.set('');
    this.activeModelIndex.set(this.model() ? -1 : 0);
    this.modelPickerOpen.set(true);
  }

  onModelInput(value: string): void {
    this.clearPreflight();
    this.model.set(value);
    this.modelQuery.set(value);
    this.modelPickerOpen.set(true);
    this.activeModelIndex.set(-1);
    const selected = this.selectedModel();
    if (selected && this.thinkingLevel() && !selected.supportedThinkingLevels.includes(this.thinkingLevel())) {
      this.thinkingLevel.set('');
    }
    if (!value.trim()) this.thinkingLevel.set('');
  }

  selectModel(candidate: ReviewModelOption | null): void {
    this.clearPreflight();
    this.model.set(candidate?.modelId ?? '');
    this.modelQuery.set('');
    if (!candidate || !candidate.supportedThinkingLevels.includes(this.thinkingLevel())) this.thinkingLevel.set('');
    this.modelPickerOpen.set(false);
    this.activeModelIndex.set(-1);
  }

  setThinkingLevel(value: string): void { this.clearPreflight(); this.thinkingLevel.set(value); }
  setCapKind(value: 'repository' | 'tokens' | 'cost'): void { this.clearPreflight(); this.capKind.set(value); }
  setCapValue(value: number | null): void { this.clearPreflight(); this.capValue.set(value); }
  setForce(value: boolean): void { this.clearPreflight(); this.force.set(value); }

  modelKeydown(event: KeyboardEvent): void {
    const options = this.filteredModels();
    if (event.key === 'Escape') {
      this.modelPickerOpen.set(false);
      this.activeModelIndex.set(-1);
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      this.modelPickerOpen.set(true);
      const count = options.length + 1;
      const direction = event.key === 'ArrowDown' ? 1 : -1;
      this.activeModelIndex.set((this.activeModelIndex() + direction + count) % count);
      return;
    }
    if (event.key === 'Enter' && this.modelPickerOpen() && this.activeModelIndex() >= 0) {
      event.preventDefault();
      const index = this.activeModelIndex();
      this.selectModel(index === 0 ? null : options[index - 1]);
    }
  }

  closeOnOutsidePointer(event: PointerEvent): void {
    if (!this.element.nativeElement.contains(event.target as Node)) this.modelPickerOpen.set(false);
  }

  optionId(index: number): string { return `${this.modelOptionsId}-option-${index}`; }

  async prepare(): Promise<void> {
    const node = this.node();
    if (!node || this.starting() || this.activeOnNode()) return;
    if (this.reviewBlockedReason()) {
      this.api.reviewError.set(this.reviewBlockedReason()!);
      return;
    }
    if (this.capKind() !== 'repository' && (!this.capValue() || this.capValue()! <= 0)) {
      this.api.reviewError.set('Enter a positive per-run cap before estimating the review.');
      return;
    }
    this.starting.set(true);
    try {
      const request: StartReviewRequest = {
        path: node.path,
        kind: this.activeKind(),
        model: this.model().trim() || null,
        cliType: this.cliType(),
        thinkingLevel: this.thinkingLevel() || null,
        tokenCap: this.capKind() === 'tokens' ? this.capValue() : null,
        costCap: this.capKind() === 'cost' ? this.capValue() : null,
        force: this.force(),
      };
      const preflight = await this.api.estimateReview(request);
      this.pendingRequest.set(request);
      this.preflight.set(preflight);
    } catch {
      // QualityApi exposes the actionable problem in reviewError for every action surface.
    } finally {
      this.starting.set(false);
    }
  }

  async start(): Promise<void> {
    const request = this.pendingRequest();
    const preflight = this.preflight();
    if (!request || !preflight || this.starting()) return;
    this.starting.set(true);
    try {
      await this.api.startReview({ ...request, confirmBelowFloor: preflight.overrideBelowFloor });
      this.clearPreflight();
      this.showLauncher.set(false);
    } catch {
      // QualityApi exposes the actionable problem in reviewError for every action surface.
    } finally {
      this.starting.set(false);
    }
  }

  useRecommendation(): void {
    const recommendation = this.preflight()?.recommendation;
    if (!recommendation) return;
    const recommendedOption = this.api.modelCatalog().models.find(candidate =>
      candidate.modelId === recommendation.recommendedModel);
    if (recommendedOption) this.cliType.set(recommendedOption.cliType);
    this.model.set(recommendation.recommendedModel);
    this.thinkingLevel.set(recommendation.recommendedThinkingLevel);
    this.clearPreflight();
    void this.prepare();
  }

  clearPreflight(): void {
    this.preflight.set(null);
    this.pendingRequest.set(null);
  }

  beginNewReview(): void { this.showLauncher.set(true); this.clearPreflight(); }

  formatNumber(value: number): string { return Math.round(value).toLocaleString(); }

  costLabel(preflight: ReviewPreflight): string {
    const estimate = preflight.estimate;
    return estimate.cost === null
      ? `Unavailable (${estimate.priceStatus})`
      : `${estimate.cost.toFixed(4)} ${estimate.currency ?? 'USD'}`;
  }

  capLabel(preflight: ReviewPreflight): string {
    if (preflight.tokenCap !== null) return `${this.formatNumber(preflight.tokenCap)} tokens`;
    if (preflight.costCap !== null) return `${preflight.costCap.toFixed(4)} ${preflight.estimate.currency ?? 'USD'}`;
    return 'Repository default: none';
  }

  runProgress(run: ReviewRun): number { return run.totalFiles ? run.completedFiles / run.totalFiles * 100 : 0; }

  runFiles(run: ReviewRun, states: string[]): typeof run.files {
    return run.files.filter(file => states.includes(file.state));
  }

  async resumeCapped(run: ReviewRun): Promise<void> {
    const current = run.tokenCap ?? run.costCap;
    const entered = prompt(`Raise the ${run.tokenCap !== null ? 'token' : 'cost'} cap to resume:`, current === null ? '' : String(current * 2));
    if (entered === null) return;
    const cap = Number(entered);
    if (!Number.isFinite(cap) || cap <= 0) return;
    await this.api.resumeReview(run.id, run.tokenCap !== null ? { tokenCap: cap } : { costCap: cap });
  }

  private countFiles(node: TreeNode | undefined): number {
    if (!node) return 0;
    if (node.level === 'file') return 1;
    return node.children.reduce((sum, child) => sum + this.countFiles(child), 0);
  }
}
