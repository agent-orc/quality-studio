import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityApi } from '../../../core/api/quality-api';
import { CoverageFact, KindState, RiskRow } from '../../../core/models/contracts';
import { FlatNode } from '../../../shared/utils/tree-utils';
import { ContainerView } from './container-view';

const unknownCoverage: CoverageFact = {
  state: 'unknown', coveredLines: 0, totalLines: 0, coveredBranches: 0, totalBranches: 0,
  linePercent: null, branchPercent: null, commit: null, measuredAt: null, filesWithData: 0,
};

function kind(state: Partial<KindState>): KindState {
  return { direct: 'missing', descendants: 'fresh', overall: 'fresh', score: null, band: null, metaPath: null, ...state };
}

function row(path: string, maxCognitive: number | null): RiskRow {
  return {
    path, name: path, gradeScore: 70, gradeBand: 'C', reviewState: 'fresh', coverage: unknownCoverage, changes: 1, riskScore: null,
    complexity: maxCognitive === null ? null : {
      language: 'typescript', cyclomatic: 12, cognitive: maxCognitive + 2, maxCyclomatic: 8, maxCognitive, functions: 3,
      pressure: Math.min(100, Math.round(maxCognitive * 100 / 30)),
      hotspots: [{ name: 'Store.load', line: 40, endLine: 80, cyclomatic: 8, cognitive: maxCognitive }],
    },
  };
}

describe('ContainerView', () => {
  let fixture: ComponentFixture<ContainerView>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ContainerView],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ContainerView);
    const api = TestBed.inject(QualityApi);
    api.risk.set({
      days: 90, currentCommit: 'abc', matrix: [],
      rows: [row('src/simple.ts', 3), row('src/tangled.ts', 22), row('src/notes.md', null)],
    });
    const projection = { score: 78, band: 'C', gradedFiles: 2, files: 5, weightedLines: 900, basis: 'size-weighted-file-grades' as const };
    const node = {
      id: 'src', name: 'src', level: 'folder', path: 'src', depth: 0, state: 'fresh', decorations: [],
      kinds: { code: kind({ projection }), security: kind({ score: 91, band: 'A', direct: 'fresh' }), performance: kind({}) },
      children: [{
        id: 'lib', name: 'lib', level: 'folder', path: 'src/lib', kinds: { code: kind({ projection }) }, coverage: unknownCoverage, children: [],
      }],
    } as FlatNode;
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('viewportHeight', 400);
    fixture.detectChanges();
  });

  it('labels a projected directory grade as a projection, never as a review', () => {
    const view = fixture.componentInstance;

    const projected = view.grade(kind({ projection: { score: 78, band: 'C', gradedFiles: 2, files: 5, weightedLines: 900, basis: 'size-weighted-file-grades' } }));
    const reviewed = view.grade(kind({ score: 91, band: 'A' }));

    expect(projected).toEqual(jasmine.objectContaining({ band: '≈C', score: '78', projected: true }));
    expect(projected.title).toContain('Projection, not a review');
    expect(projected.title).toContain('2 graded of 5 files');
    expect(reviewed).toEqual({ band: 'A', score: '91', projected: false, title: null });
    expect(view.grade(kind({}))).toEqual({ band: '-', score: '-', projected: false, title: null });

    const header = fixture.nativeElement.querySelector('.rollups .projected') as HTMLElement;
    expect(header.textContent).toContain('≈C');
    expect(header.textContent).toContain('projection');
    const cell = fixture.nativeElement.querySelector('.folder-row .grade-cell.projected') as HTMLElement;
    expect(cell.getAttribute('title')).toContain('line-weighted mean');
  });

  it('shows complexity in the risk view, sorts by it, and flags functions over the threshold', () => {
    const view = fixture.componentInstance;
    view.sortRiskBy('complexity');
    fixture.detectChanges();

    expect(view.riskRows().map(item => item.path)).toEqual(['src/tangled.ts', 'src/simple.ts', 'src/notes.md']);
    const cells = Array.from(fixture.nativeElement.querySelectorAll('.risk-row')) as HTMLElement[];
    const tangled = cells[0].querySelectorAll('[role="cell"]')[3] as HTMLElement;
    expect(tangled.textContent?.trim()).toBe('22 · Σ12');
    expect(tangled.classList).toContain('complex');
    expect(tangled.title).toContain('Store.load (line 40): cognitive 22');
    expect(view.complexityLabel(view.riskRows()[2])).toBe('—');
    expect(view.complexityTitle(view.riskRows()[2])).toContain('C#, TypeScript and JavaScript');
    expect(fixture.nativeElement.querySelector('.risk-view summary').textContent).toContain('complexity');
  });
});
