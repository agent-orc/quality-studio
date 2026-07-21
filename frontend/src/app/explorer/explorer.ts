import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { QualityApi } from '../quality-api';
import { FlatNode, ancestorIds, flattenTree } from '../tree-utils';

const ROW_HEIGHT = 30;
const TYPEAHEAD_RESET_MS = 600;

@Component({
  selector: 'qs-explorer',
  imports: [FormsModule],
  templateUrl: './explorer.html',
  styleUrl: './explorer.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Explorer {
  readonly api = inject(QualityApi);
  readonly selectedPath = input.required<string>();
  readonly viewportHeight = input.required<number>();
  readonly nodeOpen = output<string>();

  readonly expanded = signal(new Set<string>(['quality-studio', 'src', 'api']));
  readonly query = signal('');
  readonly scrollTop = signal(0);
  /** Roving-focus target: the one tree row that is a tab stop, tracked by node id so it survives DOM recycling. */
  readonly activeId = signal<string | null>(null);
  readonly treeRows = computed(() => flattenTree(this.api.tree(), this.expanded()));
  readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? flattenTree(this.api.tree(), this.expanded(), true).filter(n => n.name.toLowerCase().includes(q) || n.path.toLowerCase().includes(q)) : this.treeRows();
  });
  readonly visibleRows = computed(() => {
    const start = Math.max(0, Math.floor(this.scrollTop() / ROW_HEIGHT) - 5);
    const count = Math.ceil(this.viewportHeight() / ROW_HEIGHT) + 12;
    return this.filteredRows().slice(start, start + count).map((node, i) => ({ node, top: (start + i) * ROW_HEIGHT }));
  });
  readonly activeIndex = computed(() => {
    const id = this.activeId();
    return id === null ? -1 : this.filteredRows().findIndex(row => row.id === id);
  });
  /**
   * Logical "focus lives in the tree" flag, tracked independently of document.activeElement.
   * A recycled or entirely-replaced row (virtualization scroll, or the tree dataset itself
   * swapping under it) blurs to nowhere before this component can react, so relying on
   * document.activeElement at effect-run time would strand focus outside the tree for good.
   */
  private readonly treeHasFocus = signal(false);

  private readonly treeContainer = viewChild<ElementRef<HTMLElement>>('treeContainer');
  private typeaheadBuffer = '';
  private typeaheadTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Keep the selection visible without changing the selected container's own
    // expansion state. The chevron therefore remains a toggle-only target.
    effect(() => {
      if (!this.api.tree().length) return;
      const ancestors = ancestorIds(this.api.tree(), this.selectedPath()).slice(0, -1);
      if (ancestors.some(id => !this.expanded().has(id))) {
        this.expanded.update(current => new Set([...current, ...ancestors]));
      }
    });
    // Keeps the roving-focus target valid: picks the deep-linked row once rows exist, and
    // re-anchors it if filtering or collapsing makes the current active row disappear.
    effect(() => {
      const rows = this.filteredRows();
      if (!rows.length) return;
      const id = this.activeId();
      if (id !== null && rows.some(row => row.id === id)) return;
      const deepLinked = id === null ? rows.find(row => row.path === this.selectedPath()) : undefined;
      this.activeId.set(deepLinked?.id ?? rows[0].id);
    });
    // Virtualized rows are recycled DOM nodes: re-apply focus by node id after every render
    // instead of trusting the browser to keep it, and only when focus already lives in the tree.
    afterRenderEffect(() => {
      const id = this.activeId();
      const rows = this.visibleRows();
      if (id === null || !rows.length || !this.treeHasFocus()) return;
      const container = this.treeContainer()?.nativeElement;
      if (!container) return;
      const target = Array.from(container.querySelectorAll<HTMLElement>('[data-node-id]')).find(el => el.dataset['nodeId'] === id);
      if (target && target !== document.activeElement) target.focus({ preventScroll: true });
    });
  }

  onTreeFocusIn(): void {
    this.treeHasFocus.set(true);
  }

  onTreeFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    // A null relatedTarget usually means the previously focused row's DOM node was removed
    // (recycled out of the virtualized window, or the whole dataset swapped) rather than the
    // user deliberately leaving the tree - keep ownership so the active row gets refocused.
    if (next === null) return;
    if (this.treeContainer()?.nativeElement.contains(next)) return;
    this.treeHasFocus.set(false);
  }

  open(node: FlatNode): void {
    if (node.level !== 'file' && node.children.length) this.toggle(node);
    this.nodeOpen.emit(node.path);
  }

  expandPath(path: string): void {
    const ids = ancestorIds(this.api.tree(), path);
    this.expanded.update(current => new Set([...current, ...ids]));
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

  onRowClick(node: FlatNode): void {
    this.activeId.set(node.id);
    this.activateNode(node);
  }

  onTreeKeydown(event: KeyboardEvent): void {
    const rows = this.filteredRows();
    if (!rows.length) return;
    const index = Math.max(0, this.activeIndex());
    const node = rows[index];
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.setActive(rows, Math.min(rows.length - 1, index + 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.setActive(rows, Math.max(0, index - 1));
        break;
      case 'ArrowRight':
        event.preventDefault();
        this.arrowRight(rows, node, index);
        break;
      case 'ArrowLeft':
        event.preventDefault();
        this.arrowLeft(rows, node, index);
        break;
      case 'Home':
        event.preventDefault();
        this.setActive(rows, 0);
        break;
      case 'End':
        event.preventDefault();
        this.setActive(rows, rows.length - 1);
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.activateNode(node);
        break;
      case 'Escape':
        if (this.query()) {
          event.preventDefault();
          this.query.set('');
        }
        break;
      default:
        if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) this.typeahead(rows, event.key);
    }
  }

  private arrowRight(rows: FlatNode[], node: FlatNode, index: number): void {
    if (node.level === 'file') {
      this.fileOpen.emit(node.path);
      return;
    }
    if (!node.children.length) return;
    if (!this.expanded().has(node.id)) {
      this.toggle(node);
      return;
    }
    this.setActive(rows, Math.min(rows.length - 1, index + 1));
  }

  private arrowLeft(rows: FlatNode[], node: FlatNode, index: number): void {
    if (node.level !== 'file' && node.children.length && this.expanded().has(node.id)) {
      this.toggle(node);
      return;
    }
    for (let i = index - 1; i >= 0; i--) {
      if (rows[i].depth < node.depth) {
        this.setActive(rows, i);
        return;
      }
    }
  }

  private activateNode(node: FlatNode): void {
    if (node.level === 'file') this.fileOpen.emit(node.path);
    else if (node.children.length) this.toggle(node);
  }

  private setActive(rows: FlatNode[], index: number): void {
    const node = rows[index];
    if (!node) return;
    this.activeId.set(node.id);
    this.scrollIntoView(index);
  }

  private scrollIntoView(index: number): void {
    const top = index * ROW_HEIGHT;
    const bottom = top + ROW_HEIGHT;
    const viewTop = this.scrollTop();
    const viewBottom = viewTop + this.viewportHeight();
    const next = top < viewTop ? top : bottom > viewBottom ? bottom - this.viewportHeight() : viewTop;
    if (next === viewTop) return;
    this.scrollTop.set(next);
    const container = this.treeContainer()?.nativeElement;
    if (container) container.scrollTop = next;
  }

  private typeahead(rows: FlatNode[], key: string): void {
    this.typeaheadBuffer += key.toLowerCase();
    if (this.typeaheadTimer) clearTimeout(this.typeaheadTimer);
    this.typeaheadTimer = setTimeout(() => (this.typeaheadBuffer = ''), TYPEAHEAD_RESET_MS);
    const start = Math.max(0, this.activeIndex());
    for (let offset = 1; offset <= rows.length; offset++) {
      const i = (start + offset) % rows.length;
      if (rows[i].name.toLowerCase().startsWith(this.typeaheadBuffer)) {
        this.setActive(rows, i);
        return;
      }
    }
  }

  private measure(name: string, start: number, budget: number): void {
    const duration = performance.now() - start;
    performance.measure(name, { start, end: performance.now(), detail: { budget, path: this.selectedPath() } });
    console.info(JSON.stringify({ event: name, durationMs: +duration.toFixed(2), budgetMs: budget, withinBudget: duration < budget }));
  }
}
