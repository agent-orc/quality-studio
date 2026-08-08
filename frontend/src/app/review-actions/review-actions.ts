import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { QualityApi, ReviewKind, ReviewModelOption, TreeNode } from '../quality-api';

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
  readonly kindSelect = output<ReviewKind>();
  readonly starting = signal(false);
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
  readonly activeOnNode = computed(() => this.api.reviewRuns().some(run =>
    run.path === this.node()?.path && (run.state === 'queued' || run.state === 'running' || run.state === 'paused')));
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

  selectCli(value: string): void {
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
    this.model.set(candidate?.modelId ?? '');
    this.modelQuery.set('');
    if (!candidate || !candidate.supportedThinkingLevels.includes(this.thinkingLevel())) this.thinkingLevel.set('');
    this.modelPickerOpen.set(false);
    this.activeModelIndex.set(-1);
  }

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

  async start(): Promise<void> {
    const node = this.node();
    if (!node || this.starting() || this.activeOnNode()) return;
    if (this.capKind() !== 'repository' && (!this.capValue() || this.capValue()! <= 0)) {
      this.api.reviewError.set('Enter a positive per-run cap before estimating the review.');
      return;
    }
    this.starting.set(true);
    try {
      const request = {
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
      const estimate = preflight.estimate;
      const cost = estimate.cost === null ? `unavailable (${estimate.priceStatus})` : `${estimate.cost.toFixed(4)} ${estimate.currency ?? 'USD'}`;
      const cap = preflight.tokenCap !== null
        ? `${this.formatNumber(preflight.tokenCap)} tokens`
        : preflight.costCap !== null ? `${preflight.costCap.toFixed(4)} ${estimate.currency ?? 'USD'}` : 'none';
      const message = [
        `Start ${preflight.kind} review with ${preflight.cliType} / ${preflight.model ?? 'runner default'} / ${preflight.thinkingLevel ?? 'model default thinking'}?`,
        '',
        `${estimate.files} files · ${estimate.operations} review operations`,
        `Estimated tokens: ${this.formatNumber(estimate.inputTokens)} input + ${this.formatNumber(estimate.outputTokens)} output`,
        `Estimated cost: ${cost}`,
        `Run cap: ${cap}`,
        `History basis: ${estimate.historySamples} recorded operations`,
        '',
        'This is an estimate; actual tokenizer, context, and response length vary.',
      ].join('\n');
      if (!confirm(message)) return;
      await this.api.startReview(request);
    } catch {
      // QualityApi exposes the actionable problem in reviewError for every action surface.
    } finally {
      this.starting.set(false);
    }
  }

  private formatNumber(value: number): string { return Math.round(value).toLocaleString(); }

  private countFiles(node: TreeNode | undefined): number {
    if (!node) return 0;
    if (node.level === 'file') return 1;
    return node.children.reduce((sum, child) => sum + this.countFiles(child), 0);
  }
}
