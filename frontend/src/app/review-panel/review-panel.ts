import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { formatDateTime } from '../format';
import { FindingState, HandoverRequest, QualityApi, ReviewFinding, ReviewKind, ReviewThread } from '../quality-api';
import { FlatNode } from '../tree-utils';
import { ReviewActions } from '../review-actions/review-actions';

@Component({
  selector: 'qs-review-panel',
  imports: [ReviewActions],
  templateUrl: './review-panel.html',
  styleUrl: './review-panel.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPanel {
  readonly api = inject(QualityApi);
  readonly activeKind = input.required<ReviewKind>();
  readonly selectedPath = input.required<string>();
  readonly selectedNode = input<FlatNode | undefined>();
  readonly selectedFinding = input<ReviewFinding | null>(null);
  readonly findingSelect = output<ReviewFinding>();
  readonly kindSelect = output<ReviewKind>();

  readonly handoverStatus = signal<Record<string, string>>({});
  readonly stateAuthor = signal('Reviewer');
  readonly stateReason = signal('');
  readonly stateExpiry = signal('');
  readonly stateStatus = signal('');
  readonly threadFilter = signal<'open' | 'resolved' | 'detached'>('open');
  readonly activeMeta = computed(() => this.selectedNode()?.level === 'file'
    ? this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind()) ?? null
    : null);
  readonly activeState = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.direct ?? 'missing');
  readonly securityNodeState = computed(() => this.selectedNode()?.kinds['security']?.direct ?? 'missing');
  readonly activeInputs = computed(() => this.api.inputs()[this.activeKind()] ?? null);
  readonly metaPath = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.metaPath ?? null);
  readonly filteredThreads = computed(() => (this.activeMeta()?.threads ?? []).filter(thread =>
    this.threadFilter() === 'detached' ? thread.anchorState === 'detached' : thread.status === this.threadFilter() && thread.anchorState !== 'detached'));

  focusThread(thread: ReviewThread): void { this.api.focusedThreadId.set(thread.id); }

  threadAuthor(thread: ReviewThread): string {
    const author = thread.entries.at(-1)?.author;
    return author?.name ?? author?.agent ?? 'Reviewer';
  }

  async createTask(finding: ReviewFinding): Promise<void> {
    const key = `${this.activeKind()}:${finding.id}`;
    this.handoverStatus.update(status => ({ ...status, [key]: 'Creating…' }));
    const request: HandoverRequest = {
      findingSummary: finding.title,
      filePath: finding.locations[0]?.path ?? this.api.file()?.path ?? this.selectedPath(),
      findingText: `${finding.description}\n\nRecommendation: ${finding.recommendation}`,
      reviewKind: this.activeKind(),
      metaReference: `${this.metaPath() ?? 'review-meta'}#${finding.id}`,
    };
    try {
      const result = await this.api.createTask(request);
      this.handoverStatus.update(status => ({ ...status, [key]: result.dryRun ? 'Dry run printed' : `Created ${result.taskId}` }));
      console.info(JSON.stringify({ event: 'qs.handover.completed', findingId: key, dryRun: result.dryRun, taskId: result.taskId }));
    } catch (error) {
      this.handoverStatus.update(status => ({ ...status, [key]: 'Create failed' }));
      console.error(JSON.stringify({ event: 'qs.handover.failed', findingId: key, reason: error instanceof Error ? error.message : 'request failed' }));
    }
  }

  async setFindingState(finding: ReviewFinding, state: Exclude<FindingState, 'resolved'>): Promise<void> {
    const reason = this.stateReason().trim();
    const author = this.stateAuthor().trim();
    const path = this.api.file()?.path;
    if (!reason || !author) { this.stateStatus.set('Author and reason are required.'); return; }
    if (!path || !finding.fingerprint) { this.stateStatus.set('Finding identity is unavailable.'); return; }
    this.stateStatus.set('Saving…');
    try {
      await this.api.mutateFindingState({
        path,
        kind: this.activeKind(),
        fingerprint: finding.fingerprint,
        state,
        author,
        reason,
        expiresAt: this.stateExpiry() ? new Date(this.stateExpiry()).toISOString() : null,
        expectedTimestamp: finding.stateTimestamp ?? null,
      });
      const updated = this.api.file()?.metaDocuments
        .find(meta => meta.kind === this.activeKind())?.findings
        .find(candidate => candidate.fingerprint === finding.fingerprint);
      if (updated) this.findingSelect.emit(updated);
      this.stateReason.set(''); this.stateExpiry.set(''); this.stateStatus.set('Saved');
    } catch (error) {
      this.stateStatus.set(this.api.errorMessage(error));
    }
  }

  findingCount(state: 'open' | 'accepted' | 'waived' | 'falsePositive' | 'resolved'): number {
    return this.activeMeta()?.findingCounts?.[state] ?? 0;
  }

  scannedAt(value: string): string { return formatDateTime(value); }

  runProgress(completed: number, total: number): number { return total ? completed / total * 100 : 0; }

  formatTokens(value: number | null | undefined): string {
    if (value === null || value === undefined) return 'unavailable';
    return value >= 1_000_000 ? `${(value / 1_000_000).toFixed(1)}m tok` : value >= 1_000 ? `${(value / 1_000).toFixed(1)}k tok` : `${value} tok`;
  }

  formatDuration(value: number): string { return value >= 1000 ? `${(value / 1000).toFixed(1)}s` : `${value}ms`; }
}
