import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { formatDateTime } from '../format';
import { FindingAssessmentStatus, FindingEvidence, FindingResolutionStatus, FindingSuppressionPreview, FindingSuppressionRule, HandoverRequest, QualityApi, ReviewFinding, ReviewKind, ReviewRun, ReviewThread } from '../quality-api';
import { FlatNode } from '../tree-utils';
import { ReviewActions } from '../review-actions/review-actions';

interface CapturedExcerptView { text: string; language: string; contentHash: string; excerptHash: string; path: string; }
type FindingPolicyFilter = 'visible' | 'all' | FindingAssessmentStatus | FindingResolutionStatus | 'suppressed';

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
  readonly findingDetail = viewChild<ElementRef<HTMLElement>>('findingDetail');

  readonly handoverStatus = signal<Record<string, string>>({});
  readonly stateAuthor = signal('Reviewer');
  readonly stateReason = signal('');
  readonly stateExpiry = signal('');
  readonly stateStatus = signal('');
  readonly findingFilter = signal<FindingPolicyFilter>('visible');
  readonly scopePathPattern = signal('');
  readonly scopePreview = signal<{ rule: FindingSuppressionRule; result: FindingSuppressionPreview } | null>(null);
  readonly scopeStatus = signal('');
  readonly threadFilter = signal<'open' | 'resolved' | 'detached'>('open');
  readonly activeMeta = computed(() => this.selectedNode()?.level === 'file'
    ? this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind()) ?? null
    : null);
  readonly activeState = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.direct ?? 'missing');
  readonly activeInputs = computed(() => this.api.inputs()[this.activeKind()] ?? null);
  readonly inputTraces = computed(() => new Map(this.api.guidelineTraces().map(trace => [trace.guidelineId, trace])));
  readonly metaPath = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.metaPath ?? null);
  readonly filteredThreads = computed(() => (this.activeMeta()?.threads ?? []).filter(thread =>
    this.threadFilter() === 'detached' ? thread.anchorState === 'detached' : thread.status === this.threadFilter() && thread.anchorState !== 'detached'));
  readonly deterministicFindingCount = computed(() => (this.activeMeta()?.deterministicEvidence ?? [])
    .reduce((count, result) => count + result.findings.length, 0));
  readonly activeReviewRuns = computed(() => this.api.reviewRuns()
    .filter(run => !['done', 'failed', 'cancelled'].includes(run.state)).slice(0, 8));
  readonly visibleFindings = computed(() => {
    const findings = this.activeMeta()?.findings ?? [];
    const filter = this.findingFilter();
    if (filter === 'all') return findings;
    if (filter === 'visible') return findings.filter(finding => !finding.suppression);
    if (filter === 'suppressed') return findings.filter(finding => !!finding.suppression);
    if (['unassessed', 'confirmed', 'dismissed', 'disputed'].includes(filter))
      return findings.filter(finding => (finding.assessment?.status ?? 'unassessed') === filter);
    return findings.filter(finding => (finding.resolution?.status ?? 'open') === filter);
  });

  constructor() {
    effect(() => {
      const id = this.selectedFinding()?.id;
      if (id) queueMicrotask(() => this.findingDetail()?.nativeElement.focus());
    });
  }

  findingLocation(finding: ReviewFinding): string {
    const location = finding.locations[0];
    if (!location?.range) return location?.path ?? 'Location unavailable';
    return `${location.path}:${location.range.start.line}:${location.range.start.column}`;
  }

  findingEvidence(finding: ReviewFinding): FindingEvidence[] {
    if (Array.isArray(finding.evidence)) return finding.evidence;
    return finding.evidence ? [{ id: 'legacy', class: 'legacy-claim', status: 'claimed', summary: finding.evidence }] : [];
  }

  capturedExcerpt(finding: ReviewFinding): CapturedExcerptView | null {
    const anchor = finding.anchors?.find(candidate => candidate.role === 'primary') ?? finding.anchors?.[0];
    return anchor ? { ...anchor.capturedExcerpt, path: anchor.path } : null;
  }

  strongestEvidence(finding: ReviewFinding): string {
    return this.findingEvidence(finding)[0]?.class ?? 'evidence unknown';
  }

  originLabel(finding: ReviewFinding): string {
    const executed = finding.origin?.executed;
    if (!executed) return `${this.activeMeta()?.reviewer.agent ?? 'unknown'} · ${this.activeMeta()?.reviewer.model ?? 'unknown model'}`;
    return `${executed.cli} · ${executed.model} · ${executed.thinkingLevel ?? 'thinking unknown'}`;
  }

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

  async setFindingPolicy(finding: ReviewFinding, assessment: FindingAssessmentStatus | null, resolution: FindingResolutionStatus | null): Promise<void> {
    const reason = this.stateReason().trim();
    const author = this.stateAuthor().trim();
    const path = this.api.file()?.path;
    if (!reason || !author) { this.stateStatus.set('Author and reason are required.'); return; }
    if (!path || !finding.fingerprint) { this.stateStatus.set('Finding identity is unavailable.'); return; }
    this.stateStatus.set('Saving…');
    try {
      await this.api.mutateFindingAssessment({
        path,
        kind: this.activeKind(),
        fingerprint: finding.fingerprint,
        assessment,
        resolution,
        actor: author,
        reason,
        expectedRevision: finding.assessment?.revision ?? 0,
        reviewRunId: finding.origin?.reviewRunId ?? null,
        operationRunId: finding.origin?.operationRunId ?? null,
      });
      this.selectReloadedFinding(finding.fingerprint);
      this.stateReason.set(''); this.stateStatus.set('Saved');
    } catch (error) {
      this.stateStatus.set(this.api.errorMessage(error));
    }
  }

  async suppressExact(finding: ReviewFinding): Promise<void> {
    const reason = this.stateReason().trim();
    const author = this.stateAuthor().trim();
    const path = this.api.file()?.path;
    if (!reason || !author) { this.stateStatus.set('Author and reason are required.'); return; }
    if (!path || !finding.fingerprint) { this.stateStatus.set('Finding identity is unavailable.'); return; }
    this.stateStatus.set('Saving exact suppression…');
    try {
      await this.api.suppressFindingExact({ path, kind: this.activeKind(), fingerprint: finding.fingerprint,
        author, reason, expiresAt: this.stateExpiry() ? new Date(this.stateExpiry()).toISOString() : null,
        expectedRevision: this.activeMeta()?.suppressionRevision ?? 0 });
      this.selectReloadedFinding(finding.fingerprint);
      this.stateReason.set(''); this.stateStatus.set('Suppressed');
    } catch (error) { this.stateStatus.set(this.api.errorMessage(error)); }
  }

  async previewScopedSuppression(finding: ReviewFinding): Promise<void> {
    const reason = this.stateReason().trim();
    const author = this.stateAuthor().trim();
    const pattern = this.scopePathPattern().trim();
    if (!reason || !author || !pattern) { this.scopeStatus.set('Author, reason, and path pattern are required.'); return; }
    const rule: FindingSuppressionRule = {
      id: `scope-${Date.now().toString(36)}`, enabled: true,
      match: { ruleId: finding.ruleId, pathPattern: pattern, reviewKinds: [this.activeKind()] },
      effect: 'suppress', reason, author, createdAt: new Date().toISOString(),
      expiresAt: this.stateExpiry() ? new Date(this.stateExpiry()).toISOString() : null,
    };
    this.scopeStatus.set('Previewing…');
    try {
      const result = await this.api.previewFindingSuppression(rule);
      this.scopePreview.set({ rule, result });
      this.scopeStatus.set(`${result.matchCount} finding${result.matchCount === 1 ? '' : 's'} matched. Confirm to save.`);
    } catch (error) { this.scopeStatus.set(this.api.errorMessage(error)); }
  }

  async saveScopedSuppression(): Promise<void> {
    const preview = this.scopePreview();
    const path = this.api.file()?.path;
    if (!preview || !path) return;
    this.scopeStatus.set('Saving confirmed scope…');
    try {
      const result = await this.api.saveFindingSuppression(preview.rule, this.activeMeta()?.suppressionRevision ?? 0, path);
      this.scopePreview.set(null);
      this.scopeStatus.set(`Saved scope for ${result.matchCount} finding${result.matchCount === 1 ? '' : 's'}.`);
    } catch (error) { this.scopeStatus.set(this.api.errorMessage(error)); }
  }

  assessmentCount(status: FindingAssessmentStatus): number {
    return this.activeMeta()?.assessmentCounts?.[status] ?? 0;
  }

  findingFilterCount(filter: FindingPolicyFilter): number {
    const findings = this.activeMeta()?.findings ?? [];
    if (filter === 'all') return findings.length;
    if (filter === 'visible') return findings.filter(finding => !finding.suppression).length;
    if (filter === 'suppressed') return findings.filter(finding => !!finding.suppression).length;
    if (['unassessed', 'confirmed', 'dismissed', 'disputed'].includes(filter))
      return findings.filter(finding => (finding.assessment?.status ?? 'unassessed') === filter).length;
    return findings.filter(finding => (finding.resolution?.status ?? 'open') === filter).length;
  }

  private selectReloadedFinding(fingerprint: string): void {
    const updated = this.api.file()?.metaDocuments
      .find(meta => meta.kind === this.activeKind())?.findings
      .find(candidate => candidate.fingerprint === fingerprint);
    if (updated) this.findingSelect.emit(updated);
  }

  findingCount(state: 'open' | 'accepted' | 'waived' | 'falsePositive' | 'resolved'): number {
    return this.activeMeta()?.findingCounts?.[state] ?? 0;
  }

  scannedAt(value: string): string { return formatDateTime(value); }

  runProgress(completed: number, total: number): number { return total ? completed / total * 100 : 0; }

  historyFindingCount(run: { evidence: Array<{ findingFingerprints: string[] }> }): number {
    return new Set(run.evidence.flatMap(operation => operation.findingFingerprints)).size;
  }

  formatTokens(value: number | null | undefined): string {
    if (value === null || value === undefined) return 'unavailable';
    return value >= 1_000_000 ? `${(value / 1_000_000).toFixed(1)}m tok` : value >= 1_000 ? `${(value / 1_000).toFixed(1)}k tok` : `${value} tok`;
  }

  formatDuration(value: number): string { return value >= 1000 ? `${(value / 1000).toFixed(1)}s` : `${value}ms`; }

  spendLabel(run: ReviewRun): string {
    if (run.tokenCap !== null) {
      const spent = (run.usage.inputTokens ?? 0) + (run.usage.outputTokens ?? 0);
      return `${this.formatTokens(spent)} / ${this.formatTokens(run.tokenCap)}`;
    }
    if (run.costCap !== null) return `${this.formatCost(run.costSpent, run.currency)} / ${this.formatCost(run.costCap, run.currency)}`;
    return run.costSpent === null ? `cost ${run.priceStatus}` : this.formatCost(run.costSpent, run.currency);
  }

  async resumeCapped(run: ReviewRun): Promise<void> {
    const current = run.tokenCap ?? run.costCap;
    const entered = prompt(`Raise the ${run.tokenCap !== null ? 'token' : 'cost'} cap to resume ${run.skippedFiles} skipped file(s):`, current === null ? '' : String(current * 2));
    if (entered === null) return;
    const cap = Number(entered);
    if (!Number.isFinite(cap) || cap <= 0) return;
    await this.api.resumeReview(run.id, run.tokenCap !== null ? { tokenCap: cap } : { costCap: cap });
  }

  private formatCost(value: number | null, currency: string | null): string {
    return value === null ? 'unavailable' : `${value.toFixed(4)} ${currency ?? 'USD'}`;
  }
}
