import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';

import { ApiContext } from '../../../core/api/api-context';
import { ChangeReviewsApi } from '../../../core/api/change-reviews-api';
import {
  ChangeReviewDiffLine, ChangeReviewFinding, ChangeReviewRange, ChangeReviewStoredRecord, ChangeReviewVerdict, FindingSeverity,
} from '../../../core/models/contracts';
import { ChangeRoute } from '../../../core/navigation/change-route';

type VerdictTone = 'positive' | 'warning' | 'critical' | 'unavailable';

const VERDICTS: Record<ChangeReviewVerdict, { label: string; tone: VerdictTone; reason?: string }> = {
  pass: { label: 'Pass', tone: 'positive' },
  concerns: { label: 'Concerns', tone: 'warning' },
  block: { label: 'Block', tone: 'critical' },
  // Infrastructure outcomes say nothing about the code. They are never shown as a pass.
  'provider-unavailable': { label: 'Unavailable', tone: 'unavailable', reason: 'The review provider was unavailable.' },
  timeout: { label: 'Unavailable', tone: 'unavailable', reason: 'The reviewer timed out.' },
  'auth-failure': { label: 'Unavailable', tone: 'unavailable', reason: 'The review provider refused the sign-in.' },
  unparseable: { label: 'Unavailable', tone: 'unavailable', reason: 'The reviewer answer could not be checked against the diff.' },
};

const PRODUCERS: Record<string, string> = {
  'quality-studio': 'Quality Studio',
  'agent-studio-review-plane': 'Agent Studio Review Plane',
};

/**
 * The verdict of one commit range (deep link `view=change`). It reads the change-review store and
 * never spends on its own: "Run review" is an explicit operator action, offered only when the
 * range has no verdict and both commits exist on this host.
 */
@Component({
  selector: 'qs-change-review',
  templateUrl: './change-review.html',
  styleUrl: './change-review.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChangeReviewView {
  private readonly api = inject(ChangeReviewsApi);
  private readonly context = inject(ApiContext);

  readonly repositoryId = input.required<string>();
  readonly route = input.required<ChangeRoute>();
  readonly closed = output<void>();

  readonly severities: FindingSeverity[] = ['critical', 'high', 'medium', 'low', 'info'];
  readonly range = signal<ChangeReviewRange | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly running = signal(false);
  readonly runError = signal('');
  readonly roundOnly = signal(false);
  private sequence = 0;

  readonly missingCommits = computed(() => {
    const range = this.range();
    if (!range) return [];
    return [
      ...(range.commits.base ? [] : [{ role: 'base', sha: range.baseSha }]),
      ...(range.commits.head ? [] : [{ role: 'head', sha: range.headSha }]),
    ];
  });
  /** The verdict shown: the newest decisive record, else the newest record. */
  readonly record = computed<ChangeReviewStoredRecord | null>(() => {
    const records = this.range()?.records ?? [];
    return records.find(record => record.decisive) ?? records[0] ?? null;
  });
  readonly otherRecords = computed(() => (this.range()?.records ?? []).filter(record => record !== this.record()));
  readonly verdict = computed(() => {
    const record = this.record();
    return record ? VERDICTS[record.document.verdict] ?? { label: 'Unavailable', tone: 'unavailable' as const } : null;
  });
  readonly findings = computed<ChangeReviewFinding[]>(() => {
    const findings = this.record()?.document.findings ?? [];
    const round = this.range()?.roundOnly;
    if (!this.roundOnly() || !round?.available) return findings;
    const fingerprints = new Set(round.fingerprints);
    return findings.filter(finding => fingerprints.has(finding.fingerprint));
  });
  readonly severityCounts = computed(() => {
    const counts: Record<FindingSeverity, number> = { critical: 0, high: 0, medium: 0, low: 0, info: 0 };
    for (const finding of this.findings()) counts[finding.severity]++;
    return counts;
  });
  readonly canRunReview = computed(() => {
    const range = this.range();
    return !!range && range.permissions.runReview && range.commits.base && range.commits.head
      && !range.records.some(record => record.decisive);
  });

  constructor() {
    effect(() => {
      const repositoryId = this.repositoryId();
      const route = this.route();
      untracked(() => void this.load(repositoryId, route));
    });
  }

  async load(repositoryId = this.repositoryId(), route = this.route()): Promise<void> {
    const sequence = ++this.sequence;
    this.range.set(null);
    this.error.set('');
    this.runError.set('');
    if (!route.valid) return;
    this.loading.set(true);
    try {
      const range = await this.api.range(repositoryId, route.base, route.head, route.previous);
      if (sequence === this.sequence) this.range.set(range);
    } catch (error) {
      if (sequence === this.sequence) this.error.set(this.context.errorMessage(error));
    } finally {
      if (sequence === this.sequence) this.loading.set(false);
    }
  }

  async runReview(): Promise<void> {
    const route = this.route();
    this.running.set(true);
    this.runError.set('');
    try {
      await this.api.runReview(this.repositoryId(), route.base, route.head);
      await this.load();
    } catch (error) {
      this.runError.set(this.context.errorMessage(error));
    } finally {
      this.running.set(false);
    }
  }

  excerpt(finding: ChangeReviewFinding): ChangeReviewDiffLine[] {
    return this.range()?.excerpts[finding.fingerprint] ?? [];
  }

  short(sha: string | null | undefined): string { return sha ? sha.slice(0, 7) : '—'; }

  /** `sha256:` plus the first hex digits; enough to tell two rule sets apart at a glance. */
  shortHash(hash: string): string { return hash.replace(/^sha256:/, '').slice(0, 8); }

  producer(record: ChangeReviewStoredRecord): string { return PRODUCERS[record.provenance.producer] ?? record.provenance.producer; }

  verdictOf(record: ChangeReviewStoredRecord): string { return VERDICTS[record.document.verdict]?.label ?? 'Unavailable'; }

  linePrefix(line: ChangeReviewDiffLine): string { return line.kind === 'added' ? '+' : line.kind === 'removed' ? '-' : ' '; }

  lineNumber(line: ChangeReviewDiffLine): string { return String(line.headLine ?? line.baseLine ?? ''); }

  recordedAt(value: string): string { return new Date(value).toLocaleString(); }
}
