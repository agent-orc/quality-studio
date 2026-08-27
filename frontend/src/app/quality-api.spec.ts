import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ProjectDashboard, QualityApi, ResolvedInputs, TreeNode } from './quality-api';

const treeNode = (name: string): TreeNode => ({ id: name, name, level: 'repository', path: '.', kinds: {}, children: [] });

const dashboard = (fileCount: number): ProjectDashboard => ({
  generatedAt: '2026-08-27T10:00:00Z',
  grades: [],
  findings: { open: 0, bySeverity: { critical: 0, high: 0, medium: 0, low: 0, info: 0 }, byReviewState: { fresh: 0, stale: 0 }, path: '.' },
  staleness: { fresh: 0, stale: 0, missing: 0, total: 0, path: '.' },
  reviewCoverage: { reviewedFiles: 0, totalFiles: 0, percent: 0, path: '.' },
  testCoverage: { status: 'reported', linePercent: 0, coveredLines: 0, totalLines: 0, source: 'coverage.xml', path: '.' },
  metrics: {
    fileCount, folderCount: 0, bytes: 0, lines: 0, languages: [],
    fileSizeDistribution: [], folderSizeDistribution: [], duplicationCandidates: [], dependencyEdges: [],
  },
  hotspots: [],
} as ProjectDashboard);

describe('QualityApi', () => {
  let api: QualityApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [QualityApi, provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(QualityApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads resolved review inputs with the repository data', async () => {
    const input: ResolvedInputs = {
      kind: 'code',
      level: 'file',
      budgetCharacters: 12000,
      includedCharacters: 18,
      complete: true,
      inputs: [{
        id: 'code-style',
        source: '/global/code-style.md',
        scope: 'global',
        priority: 10,
        includedContent: 'Prefer clear names.',
        content: 'Prefer clear names.',
        truncated: false,
      }],
      omissions: [],
    };

    const loading = api.loadTree();
    http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
    http.expectOne('/api/repos/default/scan').flush({ files: [], freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0 });
    http.expectOne('/api/repos/default/inputs').flush({ kinds: { code: input } });
    http.expectOne('/api/repos/default/guidelines').flush({ guidelines: [], catalogue: [], traces: [] });
    http.expectOne('/api/repos/default/risk?days=90').flush({ days: 90, currentCommit: null, rows: [], matrix: [] });

    await new Promise(resolve => setTimeout(resolve));
    http.expectOne('/api/repos/default/handover').flush({ targetConfigured: false, dryRun: true });
    await loading;

    expect(api.connected()).toBeTrue();
    expect(api.connectionState()).toBe('live');
    expect(api.connectionLabel()).toBe('Repository connected');
    expect(api.inputs().code).toEqual(input);
    expect(api.inputs().code?.inputs[0].id).toBe('code-style');
  });

  it('keeps a live API connection when a file lookup falls back to preview content', async () => {
    const loading = api.loadTree();
    http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
    http.expectOne('/api/repos/default/scan').flush({ files: [], freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0 });
    http.expectOne('/api/repos/default/inputs').flush({ kinds: {
      code: { kind: 'code', level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] },
      security: { kind: 'security', level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] },
      performance: { kind: 'performance', level: 'file', budgetCharacters: 12000, includedCharacters: 0, complete: true, inputs: [], omissions: [] },
    } });
    http.expectOne('/api/repos/default/guidelines').flush({ guidelines: [], catalogue: [], traces: [] });
    http.expectOne('/api/repos/default/risk?days=90').flush({ days: 90, currentCommit: null, rows: [], matrix: [] });
    await new Promise(resolve => setTimeout(resolve));
    http.expectOne('/api/repos/default/handover').flush({ targetConfigured: false, dryRun: true });
    await loading;

    const fileLoading = api.loadFile('missing.cs');
    http.expectOne('/api/repos/default/file?path=missing.cs').flush('missing', { status: 404, statusText: 'Not Found' });
    await fileLoading;

    expect(api.connectionState()).toBe('live');
    expect(api.connectionLabel()).toBe('Repository connected');
    expect(api.file()?.path).toBe('missing.cs');
    expect(api.file()?.content).toContain('WebApplication.CreateBuilder');
  });

  it('revalidates an unchanged tree with If-None-Match and reuses the retained snapshot', async () => {
    const nodes = [treeNode('Quality Studio')];

    const cold = api.loadTree('default', false);
    const first = http.expectOne('/api/repos/default/tree?path=');
    expect(first.request.headers.has('If-None-Match')).toBeFalse();
    first.flush({ nodes }, { headers: { ETag: '"tree-1"' } });
    await cold;

    const warm = api.loadTree('default', false);
    const second = http.expectOne('/api/repos/default/tree?path=');
    expect(second.request.headers.get('If-None-Match')).toBe('"tree-1"');
    second.flush(null, { status: 304, statusText: 'Not Modified' });
    await warm;

    expect(api.tree()).toEqual(nodes);
    expect(api.connectionState()).toBe('live');
  });

  it('replaces the retained tree when the repository changed under the validator', async () => {
    const cold = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [treeNode('before')] }, { headers: { ETag: '"tree-1"' } });
    await cold;

    const changed = api.loadTree('default', false);
    const second = http.expectOne('/api/repos/default/tree?path=');
    expect(second.request.headers.get('If-None-Match')).toBe('"tree-1"');
    second.flush({ nodes: [treeNode('after')] }, { headers: { ETag: '"tree-2"' } });
    await changed;

    expect(api.tree().map(node => node.name)).toEqual(['after']);

    // The newly issued validator, not the superseded one, is replayed next.
    const warm = api.loadTree('default', false);
    const third = http.expectOne('/api/repos/default/tree?path=');
    expect(third.request.headers.get('If-None-Match')).toBe('"tree-2"');
    third.flush(null, { status: 304, statusText: 'Not Modified' });
    await warm;

    expect(api.tree().map(node => node.name)).toEqual(['after']);
  });

  it('never replays one repository validator against another', async () => {
    const cold = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [treeNode('default')] }, { headers: { ETag: '"tree-1"' } });
    await cold;

    const other = api.loadTree('realistic', false);
    const request = http.expectOne('/api/repos/realistic/tree?path=');
    expect(request.request.headers.has('If-None-Match')).toBeFalse();
    request.flush({ nodes: [] satisfies TreeNode[] });
    await other;
  });

  it('revalidates the project dashboard and keeps the retained snapshot on 304', async () => {
    const snapshot = dashboard(5116);

    const cold = api.loadProjectDashboard('default');
    const first = http.expectOne('/api/repos/default/project');
    expect(first.request.headers.has('If-None-Match')).toBeFalse();
    first.flush(snapshot, { headers: { ETag: '"project-1"' } });
    await cold;

    const warm = api.loadProjectDashboard('default');
    const second = http.expectOne('/api/repos/default/project');
    expect(second.request.headers.get('If-None-Match')).toBe('"project-1"');
    second.flush(null, { status: 304, statusText: 'Not Modified' });
    await warm;

    expect(api.project()?.metrics.fileCount).toBe(5116);
    expect(api.projectError()).toBe('');
    expect(api.projectLoading()).toBeFalse();
  });

  it('reports a real dashboard failure instead of silently reusing the snapshot', async () => {
    const cold = api.loadProjectDashboard('default');
    http.expectOne('/api/repos/default/project').flush(dashboard(12), { headers: { ETag: '"project-1"' } });
    await cold;

    const failing = api.loadProjectDashboard('default');
    http.expectOne('/api/repos/default/project').flush('boom', { status: 500, statusText: 'Server Error' });
    await failing;

    expect(api.projectError()).not.toBe('');

    // The validator is dropped after a failure, so recovery re-fetches a full body.
    const recovery = api.loadProjectDashboard('default');
    const retry = http.expectOne('/api/repos/default/project');
    expect(retry.request.headers.has('If-None-Match')).toBeFalse();
    retry.flush(dashboard(12), { headers: { ETag: '"project-2"' } });
    await recovery;

    expect(api.projectError()).toBe('');
  });

  it('imports repositories from Agent Studio and refreshes the registry', async () => {
    const importing = api.importFromAgentStudio();
    http.expectOne('/api/repos/import-from-agent-studio').flush({
      results: [
        { projectId: 'PROJ-002', displayName: 'Agent Studio', repositoryPath: 'C:\\Projects\\agent-taskboard-dev', status: 'imported', repositoryId: 'agent-studio', reason: null },
        { projectId: 'PROJ-016', displayName: 'Quality Studio', repositoryPath: 'C:\\Projects\\quality-studio', status: 'skipped', repositoryId: null, reason: 'Already registered.' },
      ],
      imported: 1,
      skipped: 1,
      failed: 0,
    });
    await new Promise(resolve => setTimeout(resolve));
    http.expectOne('/api/repos').flush({ repositories: [], defaultRepositoryId: 'default' });

    const result = await importing;

    expect(result.imported).toBe(1);
    expect(result.skipped).toBe(1);
    expect(result.results[0].status).toBe('imported');
    expect(result.results[1].reason).toBe('Already registered.');
  });

  it('loads repository usage and global provider quotas', async () => {
    api.connectionState.set('live');
    const usageLoading = api.loadUsage(undefined, 'code');
    http.expectOne(request => request.url === '/api/repos/default/usage' && request.params.get('kind') === 'code').flush({
      generatedAt: '2026-07-21T10:00:00Z', runs: 1, inputTokens: 100, outputTokens: 20,
      cachedInputTokens: 50, reasoningOutputTokens: 5, durationMs: 900,
      byModel: [], byKind: [], byDay: [], byReviewRun: [], recent: [],
    });
    await usageLoading;

    const quotaLoading = api.loadQuotas();
    http.expectOne('/api/quotas').flush({ at: '2026-07-21T10:00:00Z', ttlSeconds: 600, providers: [{
      provider: 'codex', plan: 'pro', fetchedAt: '2026-07-21T10:00:00Z', source: 'session-log', error: null,
      windows: [{ label: '5-hour', usedPct: 25, remainingPct: 75, used: null, limit: null, unit: '%', resetAt: null, resetLabel: 'in 2h' }],
    }] });
    await quotaLoading;

    expect(api.usage().inputTokens).toBe(100);
    expect(api.quotas().providers[0].windows[0].remainingPct).toBe(75);
  });

  it('loads the governed model catalog for review pickers', async () => {
    const loading = api.loadModelCatalog();
    http.expectOne('/api/models').flush({
      schemaVersion: 1,
      policyVersion: '2026-07-24',
      evidenceAsOfDate: '2026-07-24',
      sourceRepository: 'agent-orc/token-economy',
      sourceCommit: 'abc',
      thinkingLevels: ['medium', 'high'],
      models: [{
        modelId: 'gpt-5.6-sol', aliases: ['sol'], cliType: 'codex', capabilityTier: 'frontier',
        suitability: 'Demanding reviews.', routingStatus: 'selectable', supportedThinkingLevels: ['medium', 'high'],
        provisional: false, evidenceStatus: 'observational', note: 'Evidence note.', priceAvailable: false,
        availableForNewRuns: true,
      }],
    });
    await loading;

    expect(api.modelCatalog().policyVersion).toBe('2026-07-24');
    expect(api.modelCatalog().models[0].capabilityTier).toBe('frontier');
  });

  it('loads canonical run reports and same-scope trend pages from repository routes', async () => {
    const reportLoading = api.loadRunReport('run / 1');
    http.expectOne(request => request.url === '/api/repos/default/review/runs/run%20%2F%201/report'
      && request.params.get('format') === 'json').flush({ run: { id: 'run / 1' } });
    expect((await reportLoading).run.id).toBe('run / 1');

    const trendLoading = api.loadRunTrend('security', 'scope:src/A.cs', 'file', '30');
    http.expectOne(request => request.url === '/api/repos/default/review/runs/trend'
      && request.params.get('kind') === 'security'
      && request.params.get('scopeUnitId') === 'scope:src/A.cs'
      && request.params.get('level') === 'file'
      && request.params.get('cursor') === '30'
      && request.params.get('limit') === '30').flush({ points: [], nextCursor: null });
    expect((await trendLoading).points).toEqual([]);

    expect(api.runReportUrl('run / 1', 'sarif')).toBe('/api/repos/default/review/runs/run%20%2F%201/report?format=sarif');
    expect(api.runReportFileName('run-1', 'markdown')).toBe('quality-run-run-1.md');
  });
});
