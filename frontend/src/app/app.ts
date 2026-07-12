import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FindingSeverity, HandoverRequest, QualityApi, ReviewFinding, ReviewKind, ReviewMetaDocument, ReviewState, TreeNode } from './quality-api';

type FlatNode = TreeNode & { depth: number; state: ReviewState; decorations: { kind: string; state: ReviewState; label: string }[] };

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  readonly api = inject(QualityApi);
  readonly theme = signal<'dark' | 'light'>((new URLSearchParams(location.search).get('theme') as 'dark' | 'light') || (localStorage.getItem('qs-theme') as 'dark' | 'light') || 'dark');
  readonly expanded = signal(new Set<string>(['quality-studio', 'src', 'api']));
  readonly selected = signal('src/QualityStudio.Api/Program.cs');
  readonly query = signal('');
  readonly scrollTop = signal(0);
  readonly codeScrollTop = signal(0);
  readonly activeKind = signal<ReviewKind>('code');
  readonly selectedFinding = signal<ReviewFinding | null>(null);
  readonly handoverStatus = signal<Record<string, string>>({});
  readonly lineHeight = 22;
  readonly treeRows = computed(() => this.flatten(this.api.tree()));
  readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.flatten(this.api.tree(), true).filter(n => n.name.toLowerCase().includes(q) || n.path.toLowerCase().includes(q)) : this.treeRows();
  });
  readonly visibleRows = computed(() => {
    const start = Math.max(0, Math.floor(this.scrollTop() / 30) - 5);
    return this.filteredRows().slice(start, start + 40).map((node, i) => ({ node, top: (start + i) * 30 }));
  });
  readonly codeLines = computed(() => this.api.file()?.content.split(/\r?\n/) ?? []);
  readonly activeMeta = computed(() => this.api.file()?.metaDocuments.find(meta => meta.kind === this.activeKind()) ?? null);
  readonly availableMeta = computed(() => this.api.file()?.metaDocuments ?? []);
  readonly activeState = computed(() => this.selectedNode()?.kinds[this.activeKind()]?.direct ?? 'missing');
  readonly securityNodeState = computed(() => this.selectedNode()?.kinds['security']?.direct ?? 'missing');
  readonly securityScan = computed(() => this.api.security());
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
    const markers = this.findingsByLine();
    return this.codeLines().slice(start, start + 80).map((text, i) => ({ text, number: start + i + 1, top: (start + i) * this.lineHeight, findings: markers.get(start + i + 1) ?? [] }));
  });
  readonly selectedNode = computed(() => this.flatten(this.api.tree(), true).find(n => n.path === this.selected()));
  readonly activeInputs = computed(() => this.api.inputs()[this.activeKind()] ?? null);

  constructor() {
    effect(() => document.documentElement.dataset['theme'] = this.theme());
    this.api.loadTree();
    this.open(this.selected(), false);
  }

  toggle(node: FlatNode): void {
    const start = performance.now();
    this.expanded.update(current => {
      const next = new Set(current);
      next.has(node.id) ? next.delete(node.id) : next.add(node.id);
      return next;
    });
    requestAnimationFrame(() => this.measure('qs.tree.toggle', start, 50));
  }

  open(path: string, track = true): void {
    const start = performance.now();
    this.selected.set(path);
    this.codeScrollTop.set(0);
    this.api.loadFile(path).then(() => {
      const kinds = this.api.file()?.metaDocuments.map(meta => meta.kind) ?? [];
      if (!kinds.includes(this.activeKind())) this.activeKind.set(kinds[0] ?? 'code');
      this.selectedFinding.set(null);
      if (track) requestAnimationFrame(() => this.measure('qs.file.first-content', start, 150));
    });
  }

  selectKind(kind: ReviewKind): void {
    const start = performance.now();
    this.activeKind.set(kind);
    this.selectedFinding.set(null);
    requestAnimationFrame(() => this.measure('qs.review.aspect-switch', start, 50));
  }

  selectFinding(finding: ReviewFinding): void { this.selectedFinding.set(finding); }

  async createTask(finding: ReviewFinding): Promise<void> {
    const key = `${this.activeKind()}:${finding.id}`;
    this.handoverStatus.update(status => ({ ...status, [key]: 'Creating…' }));
    const request: HandoverRequest = {
      findingSummary: finding.title,
      filePath: finding.locations[0]?.path ?? this.api.file()?.path ?? this.selected(),
      findingText: `${finding.description}\n\nRecommendation: ${finding.recommendation}`,
      reviewKind: this.activeKind(),
      metaReference: `${this.selectedNode()?.kinds[this.activeKind()]?.metaPath ?? 'review-meta'}#${finding.id}`,
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

  findingTitle(findings: ReviewFinding[]): string { return findings.map(finding => `${finding.severity.toUpperCase()}: ${finding.title}`).join('\n'); }

  severity(findings: ReviewFinding[]): FindingSeverity { return findings[0]?.severity ?? 'info'; }

  reviewed(meta: ReviewMetaDocument): string { return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(meta.reviewedAt)); }

  scannedAt(value: string): string { return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)); }

  setTheme(): void {
    const next = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(next);
    localStorage.setItem('qs-theme', next);
  }

  private flatten(nodes: TreeNode[], all = false, depth = 0): FlatNode[] {
    const result: FlatNode[] = [];
    for (const node of nodes) {
      const state = (node.kinds['code']?.overall ?? Object.values(node.kinds)[0]?.overall ?? 'missing') as ReviewState;
      const decorations = Object.entries(node.kinds).map(([kind, value]) => ({ kind, state: value.overall, label: `${kind}: ${value.band ? `${value.band}, ` : ''}${value.overall}` }));
      result.push({ ...node, depth, state, decorations });
      if ((all || this.expanded().has(node.id)) && node.children.length) result.push(...this.flatten(node.children, all, depth + 1));
    }
    return result;
  }

  private measure(name: string, start: number, budget: number): void {
    const duration = performance.now() - start;
    performance.measure(name, { start, end: performance.now(), detail: { budget, path: this.selected() } });
    console.info(JSON.stringify({ event: name, durationMs: +duration.toFixed(2), budgetMs: budget, withinBudget: duration < budget }));
  }
}
