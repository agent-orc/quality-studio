import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ProjectDashboard, QualityApi } from '../quality-api';
import { ProjectDashboardView } from './project-dashboard';

describe('ProjectDashboardView', () => {
  let fixture: ComponentFixture<ProjectDashboardView>;
  let api: QualityApi;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDashboardView],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDashboardView);
    api = TestBed.inject(QualityApi);
    api.project.set({
      generatedAt: '2026-07-25T10:00:00Z',
      grades: ['code', 'security', 'performance'].map(kind => ({ kind, state: 'fresh', score: 90, band: 'A', path: 'src/a.ts' })),
      findings: { open: 2, bySeverity: { critical: 0, high: 1, medium: 1, low: 0, info: 0 }, byReviewState: { fresh: 2, stale: 0 }, path: 'src/a.ts' },
      staleness: { fresh: 1, stale: 0, missing: 0, total: 1, path: 'src/a.ts' },
      reviewCoverage: { reviewedFiles: 1, totalFiles: 1, percent: 100, path: 'src/a.ts' },
      testCoverage: { status: 'reported', linePercent: 80, coveredLines: 8, totalLines: 10, source: 'coverage.xml', path: 'src/a.ts' },
      metrics: {
        fileCount: 5000, folderCount: 20, bytes: 10000, lines: 2,
        languages: [{ language: 'TypeScript', files: 1, lines: 2, bytes: 20, path: 'src/a.ts' }],
        fileSizeDistribution: [{ label: '< 1 KB', count: 5000 }],
        folderSizeDistribution: [{ label: '< 1 KB', count: 20 }],
        duplicationCandidates: [],
        dependencyEdges: [],
      },
      hotspots: [{ path: 'src/a.ts', churn: 12, grade: 90, findings: 2, findingsPerKloc: 1, risk: 2 }],
    } as ProjectDashboard);
    fixture.detectChanges();
  });

  it('makes every project-health tile navigate into the tree', () => {
    const opened: string[] = [];
    fixture.componentInstance.nodeOpen.subscribe(path => opened.push(path));
    const tiles = Array.from(fixture.nativeElement.querySelectorAll('.health-card')) as HTMLButtonElement[];

    tiles.forEach(tile => tile.click());

    expect(tiles.length).toBe(8);
    expect(opened.length).toBe(tiles.length);
    expect(opened.every(path => path === 'src/a.ts')).toBeTrue();
  });

  it('renders a bounded hotspot projection for a 5000-file repository', () => {
    expect(fixture.nativeElement.textContent).toContain('5000 files');
    expect(fixture.nativeElement.querySelectorAll('.hotspot-row').length).toBe(1);
    expect(fixture.nativeElement.textContent).toContain('src/a.ts');
  });
});
