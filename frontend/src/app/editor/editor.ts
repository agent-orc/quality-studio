import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { formatBytes, formatDateTime } from '../format';
import { languageForPath } from '../language';
import { FindingSeverity, QualityApi, ReviewFinding, ReviewKind, ReviewThread } from '../quality-api';
import { FlatNode } from '../tree-utils';
import { ReviewActions } from '../review-actions/review-actions';
import { SyntaxHighlighting } from './syntax-highlighting';
import { syntaxLanguageForPath } from './syntax-language';
import { LARGE_FILE_HIGHLIGHT_LIMIT_BYTES, TokenLine, TokenSpan } from './syntax-types';

const LINE_ENDING_LABELS: Record<string, string> = { lf: 'LF', crlf: 'CRLF', mixed: 'Mixed' };
const ENCODING_LABELS: Record<string, string> = { 'utf-8': 'UTF-8', 'utf-8-bom': 'UTF-8 BOM', other: 'Unknown encoding' };
type FolderSortColumn = 'name' | ReviewKind | 'state' | 'findings' | 'reviewedAt' | 'size' | 'lines';
type SortDirection = 'asc' | 'desc';
type CodeLayoutRow =
  | { key: string; kind: 'code'; top: number; height: number; text: string; number: number; findings: ReviewFinding[] }
  | { key: string; kind: 'thread'; top: number; height: number; thread: ReviewThread; line: number; expanded: boolean }
  | { key: string; kind: 'composer'; top: number; height: number; line: number };

@Component({
  selector: 'qs-editor',
  imports: [ReviewActions],
  templateUrl: './editor.html',
  styleUrl: './editor.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Editor {
  readonly api = inject(QualityApi);
  private readonly syntaxHighlighting = inject(SyntaxHighlighting);
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
  readonly expandedThreads = signal<Record<string, boolean>>({});
  readonly composingLine = signal<number | null>(null);
  readonly drafts = signal<Record<string, string>>({});
  readonly syntaxState = signal<'plain' | 'loading' | 'ready' | 'error' | 'large'>('plain');
  private readonly syntaxCache = signal<{ path: string; lines: Array<TokenLine | undefined> }>({ path: '', lines: [] });
  private cancelSyntaxRequest: (() => void) | null = null;
  private syntaxFrame: number | null = null;
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
  readonly threadsByLine = computed(() => {
    const map = new Map<number, ReviewThread[]>();
    const path = this.api.file()?.path;
    for (const thread of this.activeMeta()?.threads ?? []) {
      if (thread.anchor.path !== path || thread.anchorState === 'detached') continue;
      const line = thread.anchor.lastKnownRange.end.line;
      map.set(line, [...(map.get(line) ?? []), thread]);
    }
    return map;
  });
  readonly layoutRows = computed<CodeLayoutRow[]>(() => {
    const rows: CodeLayoutRow[] = [];
    const markers = this.findingsByLine();
    const threads = this.threadsByLine();
    let top = 0;
    this.codeLines().forEach((text, index) => {
      const number = index + 1;
      rows.push({ key: `line:${number}`, kind: 'code', text, number, top, height: this.lineHeight, findings: markers.get(number) ?? [] });
      top += this.lineHeight;
      for (const thread of threads.get(number) ?? []) {
        const expanded = !!this.expandedThreads()[thread.id];
        const height = expanded ? 82 + thread.entries.length * 54 : 34;
        rows.push({ key: `thread:${thread.id}`, kind: 'thread', thread, line: number, top, height, expanded });
        top += height;
      }
      if (this.composingLine() === number) {
        rows.push({ key: `composer:${number}`, kind: 'composer', line: number, top, height: 86 });
        top += 86;
      }
    });
    return rows;
  });
  readonly codeSpaceHeight = computed(() => { const last = this.layoutRows().at(-1); return last ? last.top + last.height : 0; });
  readonly visibleRows = computed(() => {
    const start = Math.max(0, this.codeScrollTop() - 240);
    const end = this.codeScrollTop() + this.viewportHeight() + 400;
    return this.layoutRows().filter(row => row.top + row.height >= start && row.top <= end);
  });
  readonly topVisibleLine = computed(() => this.visibleRows().find(row => row.kind === 'code')?.number ?? 1);
  readonly pathParts = computed(() => {
    const path = this.api.file()?.path ?? '';
    const slash = path.lastIndexOf('/');
    return slash === -1 ? { directory: '', name: path } : { directory: path.slice(0, slash + 1), name: path.slice(slash + 1) };
  });
  readonly language = computed(() => languageForPath(this.api.file()?.path));
  readonly fileSizeBytes = computed(() => {
    const file = this.api.file();
    if (!file) return 0;
    return Number.isFinite(file.sizeBytes) ? file.sizeBytes : new TextEncoder().encode(file.content).byteLength;
  });
  readonly fileSizeLabel = computed(() => formatBytes(this.fileSizeBytes()));
  readonly largeFileMode = computed(() => this.fileSizeBytes() > LARGE_FILE_HIGHLIGHT_LIMIT_BYTES);
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
    effect(onCleanup => {
      const file = this.api.file();
      const selectedPath = this.selectedPath();
      const language = syntaxLanguageForPath(file?.path);
      this.cancelHighlighting();
      this.syntaxCache.set({ path: file?.path ?? '', lines: [] });
      this.syntaxState.set(file && this.largeFileMode() ? 'large' : 'plain');

      if (!file || file.path !== selectedPath || !language || this.largeFileMode()) return;
      // Wait until the browser has painted plain virtualized text. Worker startup and
      // source cloning are deliberately kept out of the first-visible-content frame.
      this.syntaxFrame = requestAnimationFrame(() => {
        this.syntaxFrame = null;
        if (this.api.file() !== file || this.selectedPath() !== file.path) return;
        this.syntaxState.set('loading');
        this.cancelSyntaxRequest = this.syntaxHighlighting.highlight(file.path, file.content, language, {
          chunk: (startLine, lines) => {
            if (this.api.file() !== file) return;
            this.syntaxCache.update(cache => {
              if (cache.path !== file.path) return cache;
              const next = cache.lines.slice();
              next.splice(startLine, lines.length, ...lines);
              return { path: cache.path, lines: next };
            });
          },
          done: () => {
            if (this.api.file() === file) this.syntaxState.set('ready');
            this.cancelSyntaxRequest = null;
          },
          error: () => {
            if (this.api.file() === file) this.syntaxState.set('error');
            this.cancelSyntaxRequest = null;
          },
        });
      });
      onCleanup(() => this.cancelHighlighting());
    });
    effect(() => {
      const id = this.api.focusedThreadId();
      if (!id) return;
      this.expandedThreads.update(value => ({ ...value, [id]: true }));
      queueMicrotask(() => {
        const row = this.layoutRows().find(candidate => candidate.kind === 'thread' && candidate.thread.id === id);
        if (row) this.codeScrollTop.set(Math.max(0, row.top - 80));
      });
    });
  }

  selectKind(kind: ReviewKind): void { this.kindSelect.emit(kind); }

  tokensForLine(line: number, text: string): TokenLine {
    const file = this.api.file();
    const cache = this.syntaxCache();
    return file && cache.path === file.path && cache.lines[line - 1]
      ? cache.lines[line - 1]!
      : [{ text, kind: 'plain' } satisfies TokenSpan];
  }

  findingTitle(findings: ReviewFinding[]): string { return findings.map(finding => `${finding.severity.toUpperCase()}: ${finding.title}`).join('\n'); }

  severity(findings: ReviewFinding[]): FindingSeverity { return findings[0]?.severity ?? 'info'; }

  toggleThread(thread: ReviewThread): void {
    this.expandedThreads.update(value => ({ ...value, [thread.id]: !value[thread.id] }));
    this.api.focusedThreadId.set(thread.id);
  }

  threadAuthor(entry: ReviewThread['entries'][number]): string { return entry.author.name ?? entry.author.agent ?? 'Reviewer'; }
  threadAuthors(thread: ReviewThread): number { return new Set(thread.entries.map(entry => this.threadAuthor(entry))).size; }
  linkedSeverity(thread: ReviewThread): FindingSeverity | null { return this.activeMeta()?.findings.find(finding => finding.fingerprint === thread.anchor.fingerprint)?.severity ?? null; }
  lastActivity(thread: ReviewThread): string { return this.reviewed(thread.entries.at(-1)?.createdAt ?? this.activeMeta()!.reviewedAt); }
  setDraft(key: string, value: string): void { this.drafts.update(drafts => ({ ...drafts, [key]: value })); }

  async addThread(line: number): Promise<void> {
    const body = this.drafts()[`line:${line}`]?.trim();
    const file = this.api.file();
    if (!body || !file) return;
    const finding = this.findingsByLine().get(line)?.[0];
    const created = await this.api.mutateThread({ path: file.path, kind: this.activeKind(), line, body, findingFingerprint: finding?.fingerprint, humanName: 'Reviewer' });
    this.composingLine.set(null); this.setDraft(`line:${line}`, ''); this.api.focusedThreadId.set(created.id);
  }

  async reply(thread: ReviewThread): Promise<void> {
    const body = this.drafts()[thread.id]?.trim();
    const file = this.api.file();
    if (!body || !file) return;
    await this.api.mutateThread({ path: file.path, kind: this.activeKind(), threadId: thread.id, body, replyTo: thread.entries.at(-1)?.id, humanName: 'Reviewer' });
    this.setDraft(thread.id, '');
  }

  async setThreadStatus(thread: ReviewThread): Promise<void> {
    const file = this.api.file(); if (!file) return;
    await this.api.mutateThread({ path: file.path, kind: this.activeKind(), threadId: thread.id, status: thread.status === 'open' ? 'resolved' : 'open' });
  }

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

  private cancelHighlighting(): void {
    if (this.syntaxFrame !== null) cancelAnimationFrame(this.syntaxFrame);
    this.syntaxFrame = null;
    this.cancelSyntaxRequest?.();
    this.cancelSyntaxRequest = null;
  }
}
