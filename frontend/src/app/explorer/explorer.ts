import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { QualityApi } from '../quality-api';
import { FlatNode, ancestorIds, flattenTree } from '../tree-utils';

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
  readonly fileOpen = output<string>();

  readonly expanded = signal(new Set<string>(['quality-studio', 'src', 'api']));
  readonly query = signal('');
  readonly scrollTop = signal(0);
  readonly treeRows = computed(() => flattenTree(this.api.tree(), this.expanded()));
  readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? flattenTree(this.api.tree(), this.expanded(), true).filter(n => n.name.toLowerCase().includes(q) || n.path.toLowerCase().includes(q)) : this.treeRows();
  });
  readonly visibleRows = computed(() => {
    const start = Math.max(0, Math.floor(this.scrollTop() / 30) - 5);
    const count = Math.ceil(this.viewportHeight() / 30) + 12;
    return this.filteredRows().slice(start, start + count).map((node, i) => ({ node, top: (start + i) * 30 }));
  });

  constructor() {
    // One-shot: once the tree is available, expand down to the deep-linked path.
    const reveal = effect(() => {
      if (!this.api.tree().length) return;
      this.expanded.update(current => new Set([...current, ...ancestorIds(this.api.tree(), this.selectedPath())]));
      reveal.destroy();
    });
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

  private measure(name: string, start: number, budget: number): void {
    const duration = performance.now() - start;
    performance.measure(name, { start, end: performance.now(), detail: { budget, path: this.selectedPath() } });
    console.info(JSON.stringify({ event: name, durationMs: +duration.toFixed(2), budgetMs: budget, withinBudget: duration < budget }));
  }
}
