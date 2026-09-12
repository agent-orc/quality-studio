import { WORKSPACE_METRICS } from '../../../shared/ui/layout-metrics';
import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { ContainerView } from '../container-view/container-view';
import { formatDateTime, formatOptionalBytes } from '../../../shared/utils/format';
import { languageForPath } from '../../../shared/utils/language';
import { QualityApi } from '../../../core/api/quality-api';
import { CoverageFact, FindingSeverity, ReviewFinding, ReviewKind, ReviewThread } from '../../../core/models/contracts';
import { FlatNode } from '../../../shared/utils/tree-utils';
import { FindingSpanRange, SegmentedSpan, segmentLineTokens } from './finding-span-segmentation';
import { SyntaxHighlighting } from './syntax-highlighting';
import { syntaxLanguageForPath } from './syntax-language';
import { LARGE_FILE_HIGHLIGHT_LIMIT_BYTES, TokenLine, TokenSpan } from './syntax-types';

const LINE_ENDING_LABELS: Record<string, string> = { lf: 'LF', crlf: 'CRLF', mixed: 'Mixed' };
const ENCODING_LABELS: Record<string, string> = { 'utf-8': 'UTF-8', 'utf-8-bom': 'UTF-8 BOM', other: 'Unknown encoding' };
type CodeLayoutRow =
  | { key: string; kind: 'code'; top: number; height: number; text: string; number: number; findings: ReviewFinding[] }
  | { key: string; kind: 'thread'; top: number; height: number; thread: ReviewThread; line: number; expanded: boolean }
  | { key: string; kind: 'composer'; top: number; height: number; line: number };

@Component({
  selector: 'qs-editor',
  host: { '[style.--studio-code-line-height.px]': 'lineHeight' },
  imports: [ContainerView],
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
  readonly selectedFinding = input<ReviewFinding | null>(null);
  readonly selectedLocationIndex = input(0);
  readonly viewportHeight = input.required<number>();
  readonly kindSelect = output<ReviewKind>();
  readonly findingSelect = output<ReviewFinding>();
  readonly nodeOpen = output<string>();

  readonly lineHeight = WORKSPACE_METRICS.codeLine;
  readonly reviewKinds: ReviewKind[] = ['code', 'security', 'performance'];
  readonly codeScrollTop = signal(0);
  readonly expandedThreads = signal<Record<string, boolean>>({});
  readonly composingLine = signal<number | null>(null);
  readonly drafts = signal<Record<string, string>>({});
  readonly syntaxState = signal<'plain' | 'loading' | 'ready' | 'error' | 'large'>('plain');
  private readonly syntaxCache = signal<{ path: string; lines: Array<TokenLine | undefined> }>({ path: '', lines: [] });
  private readonly codeViewport = viewChild<ElementRef<HTMLElement>>('codeViewport');
  private cancelSyntaxRequest: (() => void) | null = null;
  private syntaxFrame: number | null = null;
  readonly isContainer = computed(() => !!this.selectedNode() && this.selectedNode()?.level !== 'file');
  readonly codeLines = computed(() => this.api.file()?.content.split(/\r?\n/) ?? []);
  readonly activeMeta = computed(() => this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind()) ?? null);
  readonly availableMeta = computed(() => this.api.file()?.metaDocuments ?? []);
  readonly activeState = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.direct ?? 'missing');
  readonly selectedLocation = computed(() => {
    if (this.activeState() === 'stale') return null;
    const location = this.selectedFinding()?.locations[this.selectedLocationIndex()];
    if (!location?.range || location.path !== this.api.file()?.path) return null;
    return location;
  });
  readonly findingsByLine = computed(() => {
    const map = new Map<number, ReviewFinding[]>();
    const path = this.api.file()?.path;
    for (const finding of this.activeMeta()?.findings ?? []) for (const location of finding.locations) {
      // An ignored finding keeps its observation but stops colouring current source.
      if (finding.suppression) continue;
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
        const height = expanded ? Math.max(WORKSPACE_METRICS.threadExpandedMin, WORKSPACE_METRICS.threadHeader + thread.entries.length * WORKSPACE_METRICS.threadEntry) : WORKSPACE_METRICS.threadCollapsed;
        rows.push({ key: `thread:${thread.id}`, kind: 'thread', thread, line: number, top, height, expanded });
        top += height;
      }
      if (this.composingLine() === number) {
        rows.push({ key: `composer:${number}`, kind: 'composer', line: number, top, height: WORKSPACE_METRICS.composer });
        top += WORKSPACE_METRICS.composer;
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
  readonly fileSizeLabel = computed(() => formatOptionalBytes(this.fileSizeBytes()));
  readonly largeFileMode = computed(() => this.fileSizeBytes() > LARGE_FILE_HIGHLIGHT_LIMIT_BYTES);
  readonly lineEndingLabel = computed(() => LINE_ENDING_LABELS[this.api.file()?.lineEnding ?? 'lf']);
  readonly encodingLabel = computed(() => ENCODING_LABELS[this.api.file()?.encoding ?? 'utf-8']);

  constructor() {
    effect(() => { this.selectedPath(); this.codeScrollTop.set(0); });
    effect(() => {
      const range = this.selectedLocation()?.range;
      if (!range) return;
      const row = this.layoutRows().find(candidate => candidate.kind === 'code' && candidate.number === range.start.line);
      if (row) this.codeScrollTop.set(Math.max(0, row.top - Math.floor(this.viewportHeight() / 3)));
    });
    afterRenderEffect(() => {
      const fingerprint = this.selectedFinding()?.fingerprint;
      const location = this.selectedLocation();
      this.visibleRows();
      if (!fingerprint || !location) return;
      const viewport = this.codeViewport()?.nativeElement;
      const marker = viewport?.querySelector(`[data-finding-fingerprint="${CSS.escape(fingerprint)}"]`) as HTMLButtonElement | null;
      marker?.focus({ preventScroll: true });
    });
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

  /** Repeats the failed file request from the rendered error state. */
  retryFile(): void { void this.api.loadFile(this.selectedPath()); }

  tokensForLine(line: number, text: string): TokenLine {
    const file = this.api.file();
    const cache = this.syntaxCache();
    return file && cache.path === file.path && cache.lines[line - 1]
      ? cache.lines[line - 1]!
      : [{ text, kind: 'plain' } satisfies TokenSpan];
  }

  segmentedLine(line: number, text: string, findings: ReviewFinding[]): SegmentedSpan[] {
    const path = this.api.file()?.path;
    const ranges: FindingSpanRange[] = [];
    for (const finding of findings) for (const location of finding.locations) {
      if (location.path === path && location.range) ranges.push({ fingerprint: finding.fingerprint ?? finding.id, ...location.range });
    }
    const selected = this.selectedFinding();
    return segmentLineTokens(this.tokensForLine(line, text), line, text, ranges, selected ? selected.fingerprint ?? selected.id : null);
  }

  segmentClass(segment: SegmentedSpan): string {
    return segment.state === 'plain' ? `tok-${segment.kind}` : `tok-${segment.kind} tok-${segment.state}`;
  }

  segmentAriaLabel(segment: SegmentedSpan, line: number): string | null {
    return segment.state === 'plain' ? null : `${segment.state} finding span at line ${line}: ${segment.text}`;
  }

  findingTitle(findings: ReviewFinding[]): string { return findings.map(finding => `${finding.severity.toUpperCase()}: ${finding.title}`).join('\n'); }

  severity(findings: ReviewFinding[]): FindingSeverity { return findings[0]?.severity ?? 'info'; }

  isSelectedLine(line: number): boolean {
    if (this.selectedFinding()?.suppression) return false;
    const range = this.selectedLocation()?.range;
    return !!range && line >= range.start.line && line <= range.end.line;
  }

  selectedMarkerFingerprint(findings: ReviewFinding[]): string | null {
    const selected = this.selectedFinding()?.fingerprint;
    return selected && findings.some(finding => finding.fingerprint === selected)
      ? selected
      : findings[0]?.fingerprint ?? null;
  }

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

  coverageLabel(coverage: CoverageFact | null | undefined): string {
    return coverage?.linePercent == null ? 'Unknown' : `${coverage.linePercent.toFixed(coverage.linePercent % 1 ? 1 : 0)}%`;
  }

  coverageTitle(coverage: CoverageFact | null | undefined): string {
    if (!coverage || coverage.state === 'unknown') return 'No coverage data';
    const branch = coverage.totalBranches ? `; branches ${coverage.coveredBranches}/${coverage.totalBranches}` : '';
    return `${coverage.coveredLines}/${coverage.totalLines} lines${branch}; measured ${coverage.measuredAt ?? 'at an unknown time'}${coverage.state === 'stale' ? '; stale commit' : ''}`;
  }

  private cancelHighlighting(): void {
    if (this.syntaxFrame !== null) cancelAnimationFrame(this.syntaxFrame);
    this.syntaxFrame = null;
    this.cancelSyntaxRequest?.();
    this.cancelSyntaxRequest = null;
  }
}
