import { ChangeDetectionStrategy, Component, OnInit, output, inject, signal } from '@angular/core';
import { AttackCoverageCell, AttackCoverageRow, QualityApi, TreeNode } from '../quality-api';

@Component({
  selector: 'qs-attack-coverage',
  templateUrl: './attack-coverage.html',
  styleUrl: './attack-coverage.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AttackCoverage implements OnInit {
  readonly api = inject(QualityApi);
  readonly close = output<void>();
  readonly selectedCell = signal<AttackCoverageCell | null>(null);
  readonly scope = signal('.');

  async ngOnInit(): Promise<void> {
    try {
      const scope = this.containsPath(this.api.tree(), 'src/QualityStudio.Api')
        ? 'src/QualityStudio.Api'
        : '.';
      this.scope.set(scope);
      const matrix = await this.api.loadAttackCoverage(scope);
      const attention = matrix.rows.flatMap(row => row.cells).find(cell => cell.needsHumanAttention);
      this.selectedCell.set(attention ?? matrix.rows[0]?.cells[0] ?? null);
    } catch {
      // The API signal carries the user-visible error.
    }
  }

  cell(row: AttackCoverageRow, attackId: string): AttackCoverageCell | null {
    return row.cells.find(candidate => candidate.attackId === attackId) ?? null;
  }

  age(cell: AttackCoverageCell): string {
    if (cell.ageDays === null) return 'unchecked';
    if (cell.ageDays < 1) return '<1d';
    return `${Math.floor(cell.ageDays)}d`;
  }

  export(): void {
    const matrix = this.api.attackCoverage();
    if (!matrix) return;
    const blob = new Blob([JSON.stringify(matrix, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `attack-coverage-${this.api.selectedRepositoryId()}.json`;
    link.click();
    URL.revokeObjectURL(url);
  }

  private containsPath(nodes: TreeNode[], path: string): boolean {
    return nodes.some(node => node.path === path || this.containsPath(node.children, path));
  }
}
