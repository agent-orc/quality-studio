import { WORKSPACE_METRICS } from '../../../shared/ui/layout-metrics';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';

import { CoverageFact, KindState, ReviewKind, RiskRow } from '../../../core/models/contracts';
import { formatDateTime, formatOptionalBytes } from '../../../shared/utils/format';
import { QualityApi } from '../../../core/api/quality-api';
import { SortDirection, bySortValue } from '../../../shared/utils/sorting';
import { FlatNode } from '../../../shared/utils/tree-utils';

type FolderSortColumn = 'name' | ReviewKind | 'coverage' | 'state' | 'findings' | 'reviewedAt' | 'size' | 'lines';
type RiskSortColumn = 'path' | 'grade' | 'coverage' | 'complexity' | 'changes' | 'risk';

/** What a grade cell shows: the unit's own grade, or a projection from its files marked as such. */
interface GradeView { band: string; score: string; projected: boolean; title: string | null; }

/** The per-function cognitive complexity above which a function counts as hard to follow (see ComplexityMetrics.cs). */
const COGNITIVE_THRESHOLD = 15;

/**
 * What a folder or project shows instead of source: its direct children, their rolled-up review
 * state, and the risk view over grade, coverage, complexity, and churn.
 *
 * Deferred, because a container is never on the file-open path: the editor's first-content budget
 * pays for the code view only, and this component's markup and styles stay out of the entry bundle.
 */
@Component({
  selector: 'qs-container-view',
  host: { '[style.--studio-table-row-height.px]': 'folderRowHeight' },
  templateUrl: './container-view.html',
  styleUrl: './container-view.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContainerView {
  readonly api = inject(QualityApi);
  readonly node = input<FlatNode | undefined>();
  readonly nodeOpen = output<string>();

  readonly folderRowHeight = WORKSPACE_METRICS.tableRow;
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  readonly viewportHeight = input.required<number>();
  readonly folderScrollTop = signal(0);
  readonly folderSort = signal<{ column: FolderSortColumn; direction: SortDirection }>({ column: 'name', direction: 'asc' });
  readonly riskSort = signal<{ column: RiskSortColumn; direction: SortDirection }>({ column: 'risk', direction: 'desc' });

  readonly folderRows = computed(() => {
    const { column, direction } = this.folderSort();
    return [...(this.node()?.children ?? [])].sort(bySortValue(
      node => this.sortValue(node, column),
      (left, right) => left.name.localeCompare(right.name),
      direction));
  });
  readonly visibleFolderRows = computed(() => {
    const start = Math.max(0, Math.floor(this.folderScrollTop() / this.folderRowHeight) - 5);
    const count = Math.ceil(this.viewportHeight() / this.folderRowHeight) + 12;
    return this.folderRows().slice(start, start + count).map((node, index) => ({ node, top: (start + index) * this.folderRowHeight }));
  });
  readonly riskRows = computed(() => {
    const selected = this.node()?.path ?? '.';
    const prefix = selected === '.' ? '' : selected.replace(/\/+$/, '') + '/';
    const rows = this.api.risk().rows.filter(row => !prefix || row.path === selected || row.path.startsWith(prefix));
    const { column, direction } = this.riskSort();
    return [...rows].sort(bySortValue(
      row => this.riskValue(row, column),
      (left, right) => left.path.localeCompare(right.path),
      direction));
  });

  sortBy(column: FolderSortColumn): void {
    this.folderSort.update(current => current.column === column
      ? { column, direction: current.direction === 'asc' ? 'desc' : 'asc' }
      : { column, direction: 'asc' });
  }

  sortIndicator(column: FolderSortColumn): string {
    const current = this.folderSort();
    return current.column === column ? (current.direction === 'asc' ? ' ↑' : ' ↓') : '';
  }

  sortRiskBy(column: RiskSortColumn): void {
    this.riskSort.update(current => current.column === column
      ? { column, direction: current.direction === 'asc' ? 'desc' : 'asc' }
      : { column, direction: column === 'path' ? 'asc' : 'desc' });
  }

  riskSortIndicator(column: RiskSortColumn): string {
    const current = this.riskSort();
    return current.column === column ? (current.direction === 'asc' ? ' ↑' : ' ↓') : '';
  }

  riskLabel(row: RiskRow): string { return row.riskScore == null ? 'Unknown' : row.riskScore.toFixed(1); }

  /** A container without a grade of its own shows its files' size-weighted grade, marked as a projection. */
  grade(state: KindState | undefined): GradeView {
    if (state?.band) return { band: state.band, score: state.score == null ? '-' : `${state.score}`, projected: false, title: null };
    const projection = state?.projection;
    if (!projection) return { band: '-', score: '-', projected: false, title: null };
    return {
      band: `≈${projection.band}`,
      score: `${projection.score}`,
      projected: true,
      title: `Projection, not a review: line-weighted mean of ${projection.gradedFiles} graded of ${projection.files} files ` +
        `(${projection.weightedLines} lines). An aggregate review of this directory replaces it.`,
    };
  }

  complexityLabel(row: RiskRow): string {
    return row.complexity ? `${row.complexity.maxCognitive} · Σ${row.complexity.cyclomatic}` : '—';
  }

  complexityTitle(row: RiskRow): string {
    const complexity = row.complexity;
    if (!complexity) return 'Complexity is measured for C#, TypeScript and JavaScript files only';
    const hotspots = complexity.hotspots
      .map(item => `${item.name} (line ${item.line}): cognitive ${item.cognitive}, cyclomatic ${item.cyclomatic}`)
      .join('\n');
    return `Most complex function: cognitive ${complexity.maxCognitive} (threshold ${COGNITIVE_THRESHOLD}); ` +
      `cyclomatic Σ${complexity.cyclomatic} over ${complexity.functions} functions; ` +
      `risk pressure ${complexity.pressure}/100${hotspots ? '\n' + hotspots : ''}`;
  }

  overThreshold(row: RiskRow): boolean { return (row.complexity?.maxCognitive ?? 0) > COGNITIVE_THRESHOLD; }

  formatBytes(value: number | null | undefined): string { return formatOptionalBytes(value); }

  reviewedOrDash(value: string | null | undefined): string { return value ? formatDateTime(value) : '—'; }

  coverageLabel(coverage: CoverageFact | null | undefined): string {
    return coverage?.linePercent == null ? 'Unknown' : `${coverage.linePercent.toFixed(coverage.linePercent % 1 ? 1 : 0)}%`;
  }

  coverageTitle(coverage: CoverageFact | null | undefined): string {
    if (!coverage || coverage.state === 'unknown') return 'No coverage data';
    const branch = coverage.totalBranches ? `; branches ${coverage.coveredBranches}/${coverage.totalBranches}` : '';
    return `${coverage.coveredLines}/${coverage.totalLines} lines${branch}; measured ${coverage.measuredAt ?? 'at an unknown time'}${coverage.state === 'stale' ? '; stale commit' : ''}`;
  }

  findingCountsLabel(node: FlatNode['children'][number]): string {
    const counts = node.findingCounts;
    return counts ? `O ${counts.open} · A ${counts.accepted} · W ${counts.waived} · FP ${counts.falsePositive}` : `${node.findingsCount ?? 0}`;
  }

  private sortValue(node: FlatNode['children'][number], column: FolderSortColumn): string | number | null | undefined {
    if (column === 'name') return node.name;
    if (column === 'code' || column === 'security' || column === 'performance') return node.kinds[column]?.score ?? node.kinds[column]?.projection?.score;
    if (column === 'coverage') return node.coverage?.linePercent ?? null;
    if (column === 'state') return Math.max(...Object.values(node.kinds).map(kind => kind.overall === 'missing' ? 3 : kind.overall === 'stale' ? 2 : kind.overall === 'policy-drift' ? 1 : 0), 0);
    if (column === 'findings') return node.findingsCount ?? 0;
    if (column === 'reviewedAt') return node.reviewedAt ? Date.parse(node.reviewedAt) : null;
    if (column === 'size') return node.sizeBytes ?? null;
    return node.lineCount ?? null;
  }

  private riskValue(row: RiskRow, column: RiskSortColumn): string | number | null {
    if (column === 'path') return row.path;
    if (column === 'grade') return row.gradeScore;
    if (column === 'coverage') return row.coverage.linePercent;
    if (column === 'complexity') return row.complexity?.maxCognitive ?? null;
    if (column === 'changes') return row.changes;
    return row.riskScore;
  }
}
