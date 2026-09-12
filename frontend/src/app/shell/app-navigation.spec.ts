import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { App } from './app';
import { ProjectDashboard, TreeNode } from '../core/models/contracts';
import { Editor } from '../features/code/editor/editor';
import { ProjectDashboardView } from '../features/dashboard/project-dashboard/project-dashboard';
import { QualityApi } from '../core/api/quality-api';

const coveragePath = 'src/AgentOrchestrator.CodeQuality/AgentChangeDeltaReviewer.cs';
const fileNode = (path: string): TreeNode => ({
  id: path, name: path.split('/').at(-1)!, path, level: 'file', kinds: {}, children: [],
});

describe('Dashboard navigation into unloaded tree levels', () => {
  let fixture: ComponentFixture<App>;
  let app: App;
  let api: QualityApi;
  let http: HttpTestingController;

  beforeEach(async () => {
    // Keep the real dashboard, shell navigation and editor, with startup requests out of scope.
    spyOn(App.prototype as unknown as { initialize(): Promise<void> }, 'initialize').and.resolveTo();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).overrideComponent(App, {
      set: {
        imports: [ProjectDashboardView, Editor],
        template: `@if (isProjectView()) {
          <qs-project-dashboard (nodeOpen)="open($event, true, true)" />
        } @else {
          <qs-editor [selectedPath]="selected()" [selectedNode]="selectedNode()"
            [activeKind]="activeKind()" [viewportHeight]="800" />
        }`,
      },
    }).compileComponents();
    fixture = TestBed.createComponent(App);
    app = fixture.componentInstance;
    api = TestBed.inject(QualityApi);
    http = TestBed.inject(HttpTestingController);
    app.selected.set('.');
    api.connectionState.set('live');
    api.tree.set([{ id: 'root', name: 'quality-studio', path: '.', level: 'project', kinds: {}, children: [], hasChildren: true }]);
    api.project.set({
      generatedAt: '', grades: [],
      findings: { open: 0, bySeverity: { critical: 0, high: 0, medium: 0, low: 0, info: 0 }, byReviewState: { fresh: 0, stale: 0 }, path: coveragePath },
      staleness: { fresh: 0, stale: 0, missing: 1, total: 1, path: coveragePath },
      reviewCoverage: { reviewedFiles: 0, totalFiles: 1, percent: 0, path: coveragePath },
      testCoverage: { status: 'reported', linePercent: 80, coveredLines: 8, totalLines: 10, source: 'coverage.xml', path: coveragePath },
      metrics: { fileCount: 1, folderCount: 1, bytes: 100, lines: 10, languages: [], fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [] },
      hotspots: [],
    } as ProjectDashboard);
    api.projectLoading.set(false);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    http.verify();
  });

  async function resolvePath(path: string, nodes: TreeNode[]): Promise<void> {
    http.expectOne(request => request.url.endsWith('/tree/v2/search') && request.params.get('query') === path)
      .flush({ schemaVersion: 2, parentId: null, path: `search:${path}`, offset: 0, limit: 200, nextCursor: null, nodes });
    await Promise.resolve();
  }

  function flushFile(path: string): void {
    http.expectOne(request => request.url.endsWith('/file') && request.params.get('path') === path)
      .flush({ path, content: 'public class CoverageTarget {}', metaDocuments: [], sizeBytes: 30, lineEnding: 'lf', encoding: 'utf-8' });
  }

  it('opens source from Test coverage before its explorer branch has been expanded', async () => {
    const open = spyOn(app, 'open').and.callThrough();
    expect(api.nodeAt(coveragePath)).toBeUndefined();
    const tile = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.health-card'))
      .find(button => button.textContent?.includes('Test coverage'))!;

    tile.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Opening file...');
    expect(fixture.nativeElement.querySelector('.tab').textContent).toContain('AgentChangeDeltaReviewer.cs');
    await resolvePath(coveragePath, [fileNode(coveragePath)]);
    flushFile(coveragePath);
    await open.calls.mostRecent().returnValue;
    fixture.detectChanges();

    expect(app.selectedNode()?.level).toBe('file');
    expect(fixture.nativeElement.querySelector('.code-viewport').textContent).toContain('public class CoverageTarget');
    expect(fixture.nativeElement.querySelector('.tab').textContent).not.toContain('Select an item');
  });

  it('shows a missing destination as a named error instead of an empty editor', async () => {
    const opening = app.open('src/deleted.cs', false);
    await resolvePath('src/deleted.cs', []);
    http.expectOne(request => request.url.endsWith('/file')).flush({}, { status: 404, statusText: 'Not Found' });
    await opening;
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.file-error[role="alert"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.file-error').textContent).toContain('src/deleted.cs');
    expect(fixture.nativeElement.querySelector('.tab').textContent).toContain('deleted.cs');
    expect(api.file()).toBeNull();
  });

  it('does not reopen a delayed dashboard destination after another selection', async () => {
    api.tree.update(nodes => [...nodes, fileNode('src/other.cs')]);
    const first = app.open(coveragePath, false);
    const second = app.open('src/other.cs', false);
    flushFile('src/other.cs');
    await second;
    await resolvePath(coveragePath, [fileNode(coveragePath)]);
    await first;

    expect(app.selected()).toBe('src/other.cs');
    expect(api.file()?.path).toBe('src/other.cs');
    http.expectNone(request => request.url.endsWith('/file'));
  });

  it('restores an unresolved URL destination once after the API reconnects', async () => {
    const open = spyOn(app, 'open').and.callThrough();
    app.selected.set(coveragePath);
    api.connectionState.set('offline');
    fixture.detectChanges();
    api.connectionState.set('live');
    fixture.detectChanges();
    await resolvePath(coveragePath, [fileNode(coveragePath)]);
    flushFile(coveragePath);
    await open.calls.mostRecent().returnValue;
    fixture.detectChanges();

    expect(api.file()?.path).toBe(coveragePath);
    expect(open).toHaveBeenCalledTimes(1);
    api.tree.update(nodes => [...nodes]);
    fixture.detectChanges();
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('loads children for a dashboard container link outside the expanded tree', async () => {
    const path = 'src/AgentOrchestrator.CodeQuality';
    const folder: TreeNode = { id: path, path, name: 'CodeQuality', level: 'namespace', kinds: {}, children: [], hasChildren: true, childCount: 1 };
    const open = spyOn(app, 'open').and.callThrough();
    api.project.update(project => ({ ...project!, findings: { ...project!.findings, path } }));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.findings-card') as HTMLButtonElement).click();

    await resolvePath(path, [folder]);
    http.expectOne(request => request.url.endsWith('/tree/v2') && request.params.get('parentId') === folder.id)
      .flush({ schemaVersion: 2, parentId: folder.id, path, offset: 0, limit: 500, nextCursor: null, nodes: [fileNode(coveragePath)] });
    await open.calls.mostRecent().returnValue;
    fixture.detectChanges();

    expect(api.allNodes().some(node => node.path === path)).toBeFalse();
    expect(app.selectedNode()?.children.map(node => node.path)).toEqual([coveragePath]);
    expect(fixture.nativeElement.querySelector('.statusbar').textContent).toContain('1 direct children');
    await app.open(path, false);
    http.expectNone(() => true);
  });

  it('reloads a selected folder after reconnect replaces the expanded tree', async () => {
    const path = 'src/AgentOrchestrator.CodeQuality';
    const folder: TreeNode = { id: path, path, name: 'CodeQuality', level: 'namespace', kinds: {}, children: [], hasChildren: true, childCount: 1 };
    const open = spyOn(app, 'open').and.callThrough();
    const root = api.tree()[0];
    api.tree.set([{ ...root, children: [{ ...folder, children: [fileNode(coveragePath)], childrenLoaded: true }] }]);
    app.selected.set(path);
    api.connectionState.set('offline');
    fixture.detectChanges();
    api.tree.set([{ ...root, children: [] }]);
    api.connectionState.set('live');
    fixture.detectChanges();

    await resolvePath(path, [folder]);
    http.expectOne(request => request.url.endsWith('/tree/v2') && request.params.get('parentId') === folder.id)
      .flush({ schemaVersion: 2, parentId: folder.id, path, offset: 0, limit: 500, nextCursor: null, nodes: [fileNode(coveragePath)] });
    await open.calls.mostRecent().returnValue;
    fixture.detectChanges();

    expect(app.selectedNode()?.childrenLoaded).toBeTrue();
    expect(app.selectedNode()?.children.map(node => node.path)).toEqual([coveragePath]);
    expect(fixture.nativeElement.querySelector('.statusbar').textContent).toContain('1 direct children');
    expect(open).toHaveBeenCalledTimes(1);
    http.expectNone(request => request.url.endsWith('/file'));
  });

  it('keeps the project route a dashboard without trying to open it as a file', async () => {
    api.tree.set([]);
    await app.open('.', false);

    expect(app.isProjectView()).toBeTrue();
    expect(api.loading()).toBeFalse();
    http.expectNone(() => true);
  });
});
