import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { formatBytes, formatDateTime } from '../format';
import { languageForPath } from '../language';
import { FindingSeverity, QualityApi, ReviewFinding, ReviewKind } from '../quality-api';
import { FlatNode } from '../tree-utils';
import { ReviewActions } from '../review-actions/review-actions';

const LINE_ENDING_LABELS: Record<string, string> = { lf: 'LF', crlf: 'CRLF', mixed: 'Mixed' };
const ENCODING_LABELS: Record<string, string> = { 'utf-8': 'UTF-8', 'utf-8-bom': 'UTF-8 BOM', other: 'Unknown encoding' };
type FolderSortColumn = 'name' | ReviewKind | 'state' | 'findings' | 'reviewedAt' | 'size' | 'lines';
type SortDirection = 'asc' | 'desc';

@Component({
  selector: 'qs-editor',
  imports: [ReviewActions],
  templateUrl: './editor.html',
  styleUrl: './editor.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Editor {
  readonly api = inject(QualityApi);
  readonly selectedPath = input.required<string>();
  readonly activeKind = input.required<ReviewKind>();
  readonly selectedNode = input<FlatNode | undefined>();
  readonly viewportHeight = input.required<number>();
  readonly kindSelect = output<ReviewKind>();
  readonly findingSelect = output<ReviewFinding>();
  readonly nodeOpen = output<string>();

  readonly lineHeight = 22;
  readonly folderRowHeight = 38;
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  readonly codeScrollTop = signal(0);
  readonly folderScrollTop = signal(0);
  readonly folderSort = signal<{ column: FolderSortColumn; direction: SortDirection }>({ column: 'name', direction: 'asc' });
  readonly isContainer = computed(() => !!this.selectedNode() && this.selectedNode()?.level !== 'file');
  readonly codeLines = computed(() => this.api.file()?.content.split(/\r?\n/) ?? []);
  readonly activeMeta = computed(() => this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind()) ?? null);
  readonly availableMeta = computed(() => this.api.file()?.metaDocuments ?? []);
  readonly activeState = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.direct ?? 'missing');
  readonly findingsByLine = computed(() => {
    const map = new Map<number, ReviewFinding[]>();
    const path = this.api.file()?.path;
    for (const finding of this.activeMeta()?.findings ?? []) for (const location of finding.locations) {
      if (location.path !== path || !location.range) continue;
      for (let line = location.range.start.line; line <= location.range.end.line; line++) map.set(line, [...(map.get(line) ?? []), finding]);
    }
    return map;
  });
  readonly visibleLines = computed(() => {
    const start = Math.max(0, Math.floor(this.codeScrollTop() / this.lineHeight) - 10);
    const count = Math.ceil(this.viewportHeight() / this.lineHeight) + 25;
    const markers = this.findingsByLine();
    return this.codeLines().slice(start, start + count).map((text, i) => ({ text, number: start + i + 1, top: (start + i) * this.lineHeight, findings: markers.get(start + i + 1) ?? [] }));
  });
  readonly topVisibleLine = computed(() => Math.floor(this.codeScrollTop() / this.lineHeight) + 1);
  readonly pathParts = computed(() => {
    const path = this.api.file()?.path ?? '';
    const slash = path.lastIndexOf('/');
    return slash === -1 ? { directory: '', name: path } : { directory: path.slice(0, slash + 1), name: path.slice(slash + 1) };
  });
  readonly language = computed(() => languageForPath(this.api.file()?.path));
  readonly fileSizeLabel = computed(() => formatBytes(this.api.file()?.sizeBytes ?? 0));
  readonly lineEndingLabel = computed(() => LINE_ENDING_LABELS[this.api.file()?.lineEnding ?? 'lf']);
  readonly encodingLabel = computed(() => ENCODING_LABELS[this.api.file()?.encoding ?? 'utf-8']);
  readonly folderRows = computed(() => {
    const { column, direction } = this.folderSort();
    const factor = direction === 'asc' ? 1 : -1;
    return [...(this.selectedNode()?.children ?? [])].sort((left, right) => {
      const leftValue = this.sortValue(left, column);
      const rightValue = this.sortValue(right, column);
      if (leftValue == null && rightValue == null) return left.name.localeCompare(right.name);
      if (leftValue == null) return 1;
      if (rightValue == null) return -1;
      const comparison = typeof leftValue === 'number' && typeof rightValue === 'number'
        ? leftValue - rightValue
        : String(leftValue).localeCompare(String(rightValue), undefined, { numeric: true, sensitivity: 'base' });
      return (comparison || left.name.localeCompare(right.name)) * factor;
    });
  });
  readonly visibleFolderRows = computed(() => {
    const start = Math.max(0, Math.floor(this.folderScrollTop() / this.folderRowHeight) - 5);
    const count = Math.ceil(this.viewportHeight() / this.folderRowHeight) + 12;
    return this.folderRows().slice(start, start + count).map((node, index) => ({ node, top: (start + index) * this.folderRowHeight }));
  });

  constructor() {
    effect(() => { this.selectedPath(); this.codeScrollTop.set(0); this.folderScrollTop.set(0); });
  }

  selectKind(kind: ReviewKind): void { this.kindSelect.emit(kind); }

  findingTitle(findings: ReviewFinding[]): string { return findings.map(finding => `${finding.severity.toUpperCase()}: ${finding.title}`).join('\n'); }

  severity(findings: ReviewFinding[]): FindingSeverity { return findings[0]?.severity ?? 'info'; }

  reviewed(value: string): string { return formatDateTime(value); }

  sortBy(column: FolderSortColumn): void {
    this.folderSort.update(current => current.column === column
      ? { column, direction: current.direction === 'asc' ? 'desc' : 'asc' }
      : { column, direction: 'asc' });
  }

  sortIndicator(column: FolderSortColumn): string {
    const current = this.folderSort();
    return current.column === column ? (current.direction === 'asc' ? ' ↑' : ' ↓') : '';
  }

  formatBytes(value: number | null | undefined): string {
    if (value == null) return '—';
    if (value < 1024) return `${value} B`;
    if (value < 1024 * 1024) return `${(value / 1024).toFixed(value < 10240 ? 1 : 0)} KB`;
    return `${(value / 1024 / 1024).toFixed(1)} MB`;
  }

  reviewedOrDash(value: string | null | undefined): string { return value ? this.reviewed(value) : '—'; }

  private sortValue(node: FlatNode['children'][number], column: FolderSortColumn): string | number | null {
    if (column === 'name') return node.name;
    if (column === 'code' || column === 'security' || column === 'performance') return node.kinds[column]?.score;
    if (column === 'state') return Math.max(...Object.values(node.kinds).map(kind => kind.overall === 'missing' ? 2 : kind.overall === 'stale' ? 1 : 0), 0);
    if (column === 'findings') return node.findingsCount ?? 0;
    if (column === 'reviewedAt') return node.reviewedAt ? Date.parse(node.reviewedAt) : null;
    if (column === 'size') return node.sizeBytes ?? null;
    return node.lineCount ?? null;
  }
}
