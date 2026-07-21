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
  readonly model = signal('');
  readonly fileCount = computed(() => this.countFiles(this.node()));
  readonly activeOnNode = computed(() => this.api.reviewRuns().some(run =>
    run.path === this.node()?.path && (run.state === 'queued' || run.state === 'running')));
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];

  async start(): Promise<void> {
    const node = this.node();
    if (!node || this.starting() || this.activeOnNode()) return;
    if (node.level === 'project' && !confirm(`Start a ${this.activeKind()} review of this project? ${this.fileCount()} files will be reviewed.`)) return;
    this.starting.set(true);
    try {
      await this.api.startReview({ path: node.path, kind: this.activeKind(), model: this.model() || null, cliType: 'codex' });
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
