import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { formatDateTime } from '../format';
import { FindingAssessmentStatus, FindingEvidence, FindingResolutionStatus, FindingSeverity, FindingState, FindingSuppressionPreview, FindingSuppressionRule, HandoverRequest, QualityApi, ReviewFinding, ReviewKind, ReviewRun, ReviewThread, ScopeRuleView } from '../quality-api';
import { FlatNode } from '../tree-utils';
import { ReviewEvidenceApi } from './review-evidence-api';
import { ReviewDetailStyles } from './review-detail-styles';

interface LastFindingMutation {
  fingerprint: string;
  previousState: Exclude<FindingState, 'resolved'>;
  previousReason: string;
  previousExpiresAt: string | null;
  expectedTimestamp: string | null;
  appliedState: Exclude<FindingState, 'resolved'>;
}

interface CapturedExcerptView { text: string; language: string; contentHash: string; excerptHash: string; path: string; }
type FindingPolicyFilter = 'visible' | 'all' | FindingAssessmentStatus | FindingResolutionStatus | 'suppressed';

@Component({
  selector: 'qs-review-panel',
  imports: [ReviewDetailStyles],
  templateUrl: './review-panel.html',
  styleUrl: './review-panel.css',
  providers: [ReviewEvidenceApi],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPanel {
  readonly api = inject(QualityApi);
  private readonly evidenceApi = inject(ReviewEvidenceApi);
  readonly activeKind = input.required<ReviewKind>();
  readonly selectedPath = input.required<string>();
  readonly selectedNode = input<FlatNode | undefined>();
  readonly selectedFinding = input<ReviewFinding | null>(null);
  readonly findingSelect = output<ReviewFinding>();
  readonly locationSelect = output<{ finding: ReviewFinding; locationIndex: number }>();
  readonly kindSelect = output<ReviewKind>();
  readonly findingDetail = viewChild<ElementRef<HTMLElement>>('findingDetail');

  readonly handoverStatus = signal<Record<string, string>>({});
  readonly stateAuthor = signal('Reviewer');
  readonly stateReason = signal('');
  readonly stateExpiry = signal('');
  readonly stateStatus = signal('');
  readonly dispositionMode = signal<'accept' | 'dismiss' | 'reopen' | null>(null);
  readonly dismissState = signal<'waived' | 'false-positive'>('waived');
  readonly lastMutation = signal<LastFindingMutation | null>(null);
  readonly scopeManagerOpen = signal(false);
  readonly scopeAction = signal<'include' | 'exclude'>('exclude');
  readonly scopePattern = signal('');
  readonly scopeReason = signal('');
  readonly scopePreview = signal<ScopeRuleView | null>(null);
  readonly scopeStatus = signal('');
  readonly scopeExpansionConfirmed = signal(false);
  readonly editingScopeRuleIndex = signal<number | null>(null);
  readonly policyFilter = signal<FindingPolicyFilter>('visible');
  readonly scopePathPattern = signal('');
  readonly suppressionPreview = signal<{ rule: FindingSuppressionRule; result: FindingSuppressionPreview } | null>(null);
  readonly suppressionScopeStatus = signal('');
  readonly threadFilter = signal<'open' | 'resolved' | 'detached'>('open');
  readonly findingFilter = signal<'active' | FindingState | 'all'>('active');
  readonly severityFilter = signal<FindingSeverity | 'all'>('all');
  readonly findingSort = signal<'severity' | 'location' | 'title'>('severity');
  readonly runDrawerOpen = signal(false);
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
  readonly scopeRuns = computed(() => this.api.reviewRuns().filter(run =>
    run.path === this.selectedNode()?.path && run.kind === this.activeKind()));
  readonly activeReviewRuns = computed(() => this.api.reviewRuns()
    .filter(run => !['done', 'failed', 'cancelled'].includes(run.state)).slice(0, 8));
  readonly reviewHistoryEntries = computed(() => typeof this.api.reviewHistory === 'function'
    ? this.api.reviewHistory().filter(entry =>
      entry.run.scope.path === this.selectedNode()?.path && entry.run.kind === this.activeKind())
    : []);
  readonly visibleFindings = computed(() => {
    const stateFilter = this.findingFilter();
    const policyFilter = this.policyFilter();
    const severity = this.severityFilter();
    const rank: Record<FindingSeverity, number> = { critical: 0, high: 1, medium: 2, low: 3, info: 4 };
    const state = (finding: ReviewFinding) => finding.state ?? 'open';
    const path = (finding: ReviewFinding) => {
      const location = finding.locations[0];
      return `${location?.path ?? ''}:${String(location?.range?.start.line ?? 0).padStart(9, '0')}`;
    };
    return [...(this.activeMeta()?.findings ?? [])]
      .filter(finding => severity === 'all' || finding.severity === severity)
      .filter(finding => this.matchesPolicyFilter(finding, policyFilter))
      .filter(finding => {
        if (stateFilter === 'all') return true;
        if (stateFilter === 'active') return state(finding) === 'open' || state(finding) === 'accepted';
        return state(finding) === stateFilter;
      })
      .sort((left, right) => {
        if (this.findingSort() === 'title') return left.title.localeCompare(right.title);
        if (this.findingSort() === 'location') return path(left).localeCompare(path(right));
        return rank[left.severity] - rank[right.severity] || path(left).localeCompare(path(right));
      });
  });

  selectFindingLocation(finding: ReviewFinding, locationIndex = 0): void {
    this.findingSelect.emit(finding);
    if (this.activeState() === 'stale' || !finding.locations[locationIndex]?.range) return;
    this.locationSelect.emit({ finding, locationIndex });
  }

  moveFinding(direction: -1 | 1): void {
    const rows = this.visibleFindings();
    if (!rows.length) return;
    const current = rows.findIndex(finding =>
      (finding.fingerprint ?? finding.id) === (this.selectedFinding()?.fingerprint ?? this.selectedFinding()?.id));
    const next = current < 0 ? 0 : (current + direction + rows.length) % rows.length;
    this.selectFindingLocation(rows[next]);
  }

  locationLabel(finding: ReviewFinding, locationIndex = 0): string {
    const location = finding.locations[locationIndex];
    if (!location) return 'Location unavailable';
    if (this.activeState() === 'stale' || !location.range) return `${location.path} · source changed`;
    const end = location.range.end.line === location.range.start.line
      ? `-${location.range.end.column}`
      : `-${location.range.end.line}:${location.range.end.column}`;
    return `${location.path}:${location.range.start.line}:${location.range.start.column}${end}`;
  }

  constructor() {
    effect(() => {
      const id = this.selectedFinding()?.id;
      if (id) queueMicrotask(() => this.findingDetail()?.nativeElement.focus());
    });
  }

  findingLocation(finding: ReviewFinding): string {
    return this.locationLabel(finding);
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
      await this.evidenceApi.mutateAssessment({
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
      if ((error as { status?: number }).status === 409) {
        await this.api.loadFile(path);
        const current = this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind())?.findings
          .find(candidate => candidate.fingerprint === finding.fingerprint);
        if (current) this.findingSelect.emit(current);
        this.stateStatus.set('This finding changed elsewhere. Current state was reloaded; review it and try again.');
      } else {
        this.stateStatus.set(this.api.errorMessage(error));
      }
    }
  }

  async setFindingState(finding: ReviewFinding, state: Exclude<FindingState, 'resolved'>): Promise<void> {
    const reason = this.stateReason().trim();
    const author = this.stateAuthor().trim();
    const path = this.api.file()?.path;
    if (!reason || !author) { this.stateStatus.set('Author and reason are required.'); return; }
    if (!path || !finding.fingerprint) { this.stateStatus.set('Finding identity is unavailable.'); return; }
    const previousState = finding.state && finding.state !== 'resolved' ? finding.state : 'open';
    this.stateStatus.set('Saving…');
    try {
      const updated = await this.api.mutateFindingState({
        path,
        kind: this.activeKind(),
        fingerprint: finding.fingerprint,
        state,
        author,
        reason,
        expiresAt: state === 'waived' && this.stateExpiry()
          ? new Date(this.stateExpiry()).toISOString()
          : null,
        expectedTimestamp: finding.stateTimestamp ?? null,
      });
      if (updated) this.findingSelect.emit(updated);
      this.lastMutation.set({
        fingerprint: finding.fingerprint,
        previousState,
        previousReason: finding.stateReason ?? 'Restored by undo.',
        previousExpiresAt: finding.stateExpiresAt ?? null,
        expectedTimestamp: updated?.stateTimestamp ?? null,
        appliedState: state,
      });
      setTimeout(() => {
        if (this.lastMutation()?.fingerprint === finding.fingerprint) this.lastMutation.set(null);
      }, 8000);
      this.stateReason.set('');
      this.stateExpiry.set('');
      this.dispositionMode.set(null);
      this.stateStatus.set('Saved');
    } catch (error) {
      if ((error as { status?: number }).status === 409) {
        await this.api.loadFile(path);
        const current = this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind())?.findings
          .find(candidate => candidate.fingerprint === finding.fingerprint);
        if (current) this.findingSelect.emit(current);
        this.stateStatus.set('This finding changed elsewhere. Current state was reloaded; review it and try again.');
      } else {
        this.stateStatus.set(this.api.errorMessage(error));
      }
    }
  }

  openDisposition(mode: 'accept' | 'dismiss' | 'reopen'): void {
    this.dispositionMode.set(mode);
    this.stateReason.set('');
    this.stateExpiry.set('');
    this.stateStatus.set('');
  }

  saveDisposition(finding: ReviewFinding): Promise<void> {
    const mode = this.dispositionMode();
    const state = mode === 'accept' ? 'accepted' : mode === 'dismiss' ? this.dismissState() : 'open';
    return this.setFindingState(finding, state);
  }

  async undoFindingState(finding: ReviewFinding): Promise<void> {
    const undo = this.lastMutation();
    const path = this.api.file()?.path;
    if (!undo || !path || !finding.fingerprint || undo.fingerprint !== finding.fingerprint) return;
    this.stateStatus.set('Undoing…');
    try {
      const updated = await this.api.mutateFindingState({
        path,
        kind: this.activeKind(),
        fingerprint: finding.fingerprint,
        state: undo.previousState,
        author: this.stateAuthor().trim() || 'Reviewer',
        reason: `Undo: ${undo.previousReason}`,
        expiresAt: undo.previousExpiresAt,
        expectedTimestamp: undo.expectedTimestamp,
      });
      if (updated) this.findingSelect.emit(updated);
      this.lastMutation.set(null);
      this.stateStatus.set('Undone');
    } catch (error) {
      this.stateStatus.set((error as { status?: number }).status === 409
        ? 'Undo could not be applied because the finding changed again. Reload and review the current state.'
        : this.api.errorMessage(error));
    }
  }

  async openScopeManager(finding?: ReviewFinding): Promise<void> {
    this.scopeManagerOpen.set(true);
    this.scopeAction.set('exclude');
    this.scopePattern.set(finding?.locations[0]?.path ?? '');
    this.scopeReason.set(finding ? `Ignore path after finding: ${finding.title}` : '');
    this.scopePreview.set(null);
    this.scopeExpansionConfirmed.set(false);
    this.editingScopeRuleIndex.set(null);
    this.scopeStatus.set('Loading scope rules…');
    try {
      await this.api.loadScopeRules();
      this.scopeStatus.set('');
      if (finding) await this.previewScopeRule();
    } catch (error) {
      this.scopeStatus.set(this.api.errorMessage(error));
    }
  }

  async previewScopeRule(): Promise<void> {
    this.scopeStatus.set('Previewing…');
    try {
      this.scopePreview.set(await this.api.previewScopeRule({
        action: this.scopeAction(), pattern: this.scopePattern(), reason: this.scopeReason() || null,
      }));
      this.scopeExpansionConfirmed.set(false);
      this.scopeStatus.set('');
    } catch (error) {
      this.scopePreview.set(null);
      this.scopeStatus.set(this.api.errorMessage(error));
    }
  }

  async saveScopeRule(): Promise<void> {
    const preview = this.scopePreview();
    if (!preview) { await this.previewScopeRule(); return; }
    if (preview.widerPattern && !this.scopeExpansionConfirmed()) {
      this.scopeStatus.set('Confirm the wider pattern after reviewing every matched path.');
      return;
    }
    this.scopeStatus.set('Saving rule…');
    try {
      const request = {
        action: this.scopeAction(), pattern: this.scopePattern(), reason: this.scopeReason() || null,
        confirmExpansion: this.scopeExpansionConfirmed(),
      };
      const editingIndex = this.editingScopeRuleIndex();
      if (editingIndex === null) await this.api.addScopeRule(request);
      else await this.api.updateScopeRule(editingIndex, request);
      this.scopeStatus.set(`Scope rule ${editingIndex === null ? 'saved' : 'updated'}. It applies to future reviews.`);
      this.scopePreview.set(null);
      this.editingScopeRuleIndex.set(null);
    } catch (error) {
      this.scopeStatus.set(this.api.errorMessage(error));
    }
  }

  async editScopeRule(rule: ScopeRuleView): Promise<void> {
    this.editingScopeRuleIndex.set(rule.index);
    this.scopeAction.set(rule.action);
    this.scopePattern.set(rule.pattern);
    this.scopeReason.set(rule.reason ?? '');
    this.scopePreview.set(null);
    this.scopeExpansionConfirmed.set(false);
    await this.previewScopeRule();
  }

  cancelScopeRuleEdit(): void {
    this.editingScopeRuleIndex.set(null);
    this.scopePreview.set(null);
    this.scopeAction.set('exclude');
    this.scopePattern.set('');
    this.scopeReason.set('');
    this.scopeStatus.set('');
  }

  async deleteScopeRule(index: number): Promise<void> {
    this.scopeStatus.set('Removing rule…');
    try {
      await this.api.deleteScopeRule(index);
      this.scopeStatus.set('Rule removed. Matching paths are eligible on the next review.');
    } catch (error) {
      this.scopeStatus.set(this.api.errorMessage(error));
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
      await this.evidenceApi.suppressExact({ path, kind: this.activeKind(), fingerprint: finding.fingerprint,
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
    if (!reason || !author || !pattern) { this.suppressionScopeStatus.set('Author, reason, and path pattern are required.'); return; }
    const rule: FindingSuppressionRule = {
      id: `scope-${Date.now().toString(36)}`, enabled: true,
      match: { ruleId: finding.ruleId, pathPattern: pattern, reviewKinds: [this.activeKind()] },
      effect: 'suppress', reason, author, createdAt: new Date().toISOString(),
      expiresAt: this.stateExpiry() ? new Date(this.stateExpiry()).toISOString() : null,
    };
    this.suppressionScopeStatus.set('Previewing…');
    try {
      const result = await this.evidenceApi.previewSuppression(rule);
      this.suppressionPreview.set({ rule, result });
      this.suppressionScopeStatus.set(`${result.matchCount} finding${result.matchCount === 1 ? '' : 's'} matched. Confirm to save.`);
    } catch (error) { this.suppressionScopeStatus.set(this.api.errorMessage(error)); }
  }

  async saveScopedSuppression(): Promise<void> {
    const preview = this.suppressionPreview();
    const path = this.api.file()?.path;
    if (!preview || !path) return;
    this.suppressionScopeStatus.set('Saving confirmed scope…');
    try {
      const result = await this.evidenceApi.saveSuppression(preview.rule, this.activeMeta()?.suppressionRevision ?? 0, path);
      this.suppressionPreview.set(null);
      this.suppressionScopeStatus.set(`Saved scope for ${result.matchCount} finding${result.matchCount === 1 ? '' : 's'}.`);
    } catch (error) { this.suppressionScopeStatus.set(this.api.errorMessage(error)); }
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

  private matchesPolicyFilter(finding: ReviewFinding, filter: FindingPolicyFilter): boolean {
    if (filter === 'all') return true;
    if (filter === 'visible') return !finding.suppression;
    if (filter === 'suppressed') return !!finding.suppression;
    if (['unassessed', 'confirmed', 'dismissed', 'disputed'].includes(filter))
      return (finding.assessment?.status ?? 'unassessed') === filter;
    return (finding.resolution?.status ?? 'open') === filter;
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
