import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { QualityApi, ReviewKind, TreeNode } from '../quality-api';

@Component({
  selector: 'qs-review-actions',
  imports: [FormsModule],
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
  readonly cliType = signal('codex');
  readonly model = signal('');
  readonly capKind = signal<'repository' | 'tokens' | 'cost'>('repository');
  readonly capValue = signal<number | null>(null);
  readonly fileCount = computed(() => this.countFiles(this.node()));
  readonly activeOnNode = computed(() => this.api.reviewRuns().some(run =>
    run.path === this.node()?.path && (run.state === 'queued' || run.state === 'running' || run.state === 'paused')));
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];

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
        tokenCap: this.capKind() === 'tokens' ? this.capValue() : null,
        costCap: this.capKind() === 'cost' ? this.capValue() : null,
      };
      const preflight = await this.api.estimateReview(request);
      const estimate = preflight.estimate;
      const cost = estimate.cost === null ? `unavailable (${estimate.priceStatus})` : `${estimate.cost.toFixed(4)} ${estimate.currency ?? 'USD'}`;
      const cap = preflight.tokenCap !== null
        ? `${this.formatNumber(preflight.tokenCap)} tokens`
        : preflight.costCap !== null ? `${preflight.costCap.toFixed(4)} ${estimate.currency ?? 'USD'}` : 'none';
      const message = [
        `Start ${preflight.kind} review with ${preflight.cliType} / ${preflight.model ?? 'runner default'}?`,
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
