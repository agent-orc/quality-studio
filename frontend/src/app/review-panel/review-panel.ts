import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { formatDateTime } from '../format';
import { FindingSeverity, FindingState, HandoverRequest, QualityApi, QualityRunComparison, QualityRunComparisonFinding, QualityRunReport, QualityRunTrendPoint, ReviewFinding, ReviewKind, ReviewRun, ReviewThread, RunReportFormat, ScopeRuleView } from '../quality-api';
import { FlatNode } from '../tree-utils';

interface LastFindingMutation {
  fingerprint: string;
  previousState: Exclude<FindingState, 'resolved'>;
  previousReason: string;
  previousExpiresAt: string | null;
  expectedTimestamp: string | null;
  appliedState: Exclude<FindingState, 'resolved'>;
}

@Component({
  selector: 'qs-review-panel',
  imports: [],
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
  readonly locationSelect = output<{ finding: ReviewFinding; locationIndex: number }>();
  readonly kindSelect = output<ReviewKind>();

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
  readonly threadFilter = signal<'open' | 'resolved' | 'detached'>('open');
  readonly findingFilter = signal<'active' | FindingState | 'all'>('active');
  readonly severityFilter = signal<FindingSeverity | 'all'>('all');
  readonly findingSort = signal<'severity' | 'location' | 'title'>('severity');
  readonly runDrawerOpen = signal(false);
  readonly selectedRunId = signal<string | null>(null);
  readonly runReport = signal<QualityRunReport | null>(null);
  readonly runTrend = signal<QualityRunTrendPoint[]>([]);
  readonly runTrendCursor = signal<string | null>(null);
  readonly runDetailLoading = signal(false);
  readonly runDetailError = signal('');
  readonly comparisonOpen = signal(false);
  readonly comparisonBaselineId = signal<string | null>(null);
  readonly comparisonCandidateId = signal<string | null>(null);
  readonly comparison = signal<QualityRunComparison | null>(null);
  readonly comparisonLoading = signal(false);
  readonly comparisonError = signal('');
  readonly runFormats: RunReportFormat[] = ['html', 'markdown', 'sarif', 'json'];
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
  readonly selectedRun = computed(() => this.scopeRuns().find(run => run.id === this.selectedRunId()) ?? null);
  readonly runFindings = computed(() => (this.runReport()?.observations ?? [])
    .flatMap(observation => observation.findings)
    .filter(finding => finding.state !== 'resolved'));
  readonly comparableTrend = computed(() => this.runTrend()
    .filter(point => point.comparable)
    .sort((left, right) => Date.parse(right.finishedAt) - Date.parse(left.finishedAt)));
  readonly comparisonBaselines = computed(() => {
    const candidate = this.comparableTrend().find(point => point.runId === this.comparisonCandidateId());
    if (!candidate) return [];
    return this.comparableTrend().filter(point => point.runId !== candidate.runId &&
      Date.parse(point.finishedAt) <= Date.parse(candidate.finishedAt));
  });
  readonly visibleFindings = computed(() => {
    const stateFilter = this.findingFilter();
    const severity = this.severityFilter();
    const rank: Record<FindingSeverity, number> = { critical: 0, high: 1, medium: 2, low: 3, info: 4 };
    const state = (finding: ReviewFinding) => finding.state ?? 'open';
    const path = (finding: ReviewFinding) => {
      const location = finding.locations[0];
      return `${location?.path ?? ''}:${String(location?.range?.start.line ?? 0).padStart(9, '0')}`;
    };
    return [...(this.activeMeta()?.findings ?? [])]
      .filter(finding => severity === 'all' || finding.severity === severity)
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
    const end = location.range.end.line === location.range.start.line ? '' : `-${location.range.end.line}`;
    return `${location.path}:${location.range.start.line}${end}`;
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
      this.stateReason.set(''); this.stateExpiry.set(''); this.dispositionMode.set(null); this.stateStatus.set('Saved');
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

  spendLabel(run: ReviewRun): string {
    if (run.tokenCap !== null) {
      const spent = (run.usage.inputTokens ?? 0) + (run.usage.outputTokens ?? 0);
      return `${this.formatTokens(spent)} / ${this.formatTokens(run.tokenCap)}`;
    }
    if (run.costCap !== null) return `${this.formatCost(run.costSpent, run.currency)} / ${this.formatCost(run.costCap, run.currency)}`;
    return run.costSpent === null ? `cost ${run.priceStatus}` : this.formatCost(run.costSpent, run.currency);
  }

  async openRun(run: ReviewRun): Promise<void> {
    this.selectedRunId.set(run.id);
    this.runReport.set(null);
    this.runTrend.set([]);
    this.runTrendCursor.set(null);
    this.runDetailError.set('');
    if (!['done', 'failed', 'cancelled', 'capped'].includes(run.state)) return;
    this.runDetailLoading.set(true);
    try {
      const scopeUnitId = this.selectedNode()?.id;
      const [report, trend] = await Promise.all([
        this.api.loadRunReport(run.id),
        scopeUnitId ? this.api.loadRunTrend(run.kind, scopeUnitId, run.level) : Promise.resolve(null),
      ]);
      this.runReport.set(report);
      this.runTrend.set(trend?.points ?? []);
      this.runTrendCursor.set(trend?.nextCursor ?? null);
    } catch (error) {
      this.runDetailError.set(this.api.errorMessage(error));
    } finally {
      this.runDetailLoading.set(false);
    }
  }

  closeRun(): void {
    this.closeComparison();
    this.selectedRunId.set(null);
    this.runReport.set(null);
    this.runTrend.set([]);
    this.runTrendCursor.set(null);
    this.runDetailError.set('');
  }

  async loadOlderTrend(): Promise<void> {
    const cursor = this.runTrendCursor();
    const run = this.selectedRun();
    const scopeUnitId = this.selectedNode()?.id;
    if (!cursor || !run || !scopeUnitId) return;
    try {
      const page = await this.api.loadRunTrend(run.kind, scopeUnitId, run.level, cursor);
      this.runTrend.update(points => [...points, ...page.points]);
      this.runTrendCursor.set(page.nextCursor);
    } catch (error) {
      this.runDetailError.set(this.api.errorMessage(error));
    }
  }

  reportUrl(runId: string, format: RunReportFormat): string { return this.api.runReportUrl(runId, format); }

  reportFileName(runId: string, format: RunReportFormat): string { return this.api.runReportFileName(runId, format); }

  trendScoreWidth(point: QualityRunTrendPoint): number { return point.score ?? 0; }

  async openComparison(): Promise<void> {
    const points = this.comparableTrend();
    if (points.length < 2) return;
    let candidate = points.find(point => point.runId === this.selectedRunId()) ?? points[0];
    let baseline = points.find(point => point.runId !== candidate.runId &&
      Date.parse(point.finishedAt) <= Date.parse(candidate.finishedAt));
    if (!baseline) {
      candidate = points[0];
      baseline = points[1];
    }
    this.comparisonCandidateId.set(candidate.runId);
    this.comparisonBaselineId.set(baseline.runId);
    this.comparisonOpen.set(true);
    await this.loadComparison();
  }

  async selectComparisonCandidate(runId: string): Promise<void> {
    this.comparisonCandidateId.set(runId);
    this.comparisonBaselineId.set(this.comparisonBaselines()[0]?.runId ?? null);
    this.comparison.set(null);
    if (this.comparisonBaselineId()) await this.loadComparison();
  }

  async loadComparison(): Promise<void> {
    const baselineId = this.comparisonBaselineId();
    const candidateId = this.comparisonCandidateId();
    if (!baselineId || !candidateId) return;
    this.comparisonLoading.set(true);
    this.comparisonError.set('');
    try {
      this.comparison.set(await this.api.compareRuns(baselineId, candidateId));
    } catch (error) {
      this.comparison.set(null);
      this.comparisonError.set(this.api.errorMessage(error));
    } finally {
      this.comparisonLoading.set(false);
    }
  }

  closeComparison(): void {
    this.comparisonOpen.set(false);
    this.comparison.set(null);
    this.comparisonError.set('');
  }

  comparisonLocation(finding: QualityRunComparisonFinding): string {
    const location = finding.locations[0];
    if (!location) return 'Location unavailable';
    return `${location.path}${location.startLine === null ? '' : `:${location.startLine}`}`;
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
