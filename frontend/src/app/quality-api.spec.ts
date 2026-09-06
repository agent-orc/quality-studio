import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QualityApi } from './quality-api';
import { ProjectDashboard, ResolvedInputs, TreeNode } from './contracts';

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

  it('reuses a retained tree snapshot on 304 and completes the live transition', async () => {
    const retainedNodes = [{ id: 'old', name: 'Old tree', level: 'project', path: '.', kinds: {}, children: [] }] satisfies TreeNode[];
    const initial = api.loadTree('default', false, 'src/app');
    const initialRequest = http.expectOne('/api/repos/default/tree?path=src%2Fapp');
    expect(initialRequest.request.headers.has('If-None-Match')).toBeFalse();
    initialRequest.flush({ nodes: retainedNodes }, { headers: { ETag: '"tree-v1"' } });
    await initial;

    api.repositoryTransition.set({ repositoryId: 'default', hasSnapshot: true });
    api.connectionState.set('connecting');
    const revalidation = api.loadTree('default', false, 'src/app');
    const conditionalRequest = http.expectOne('/api/repos/default/tree?path=src%2Fapp');
    expect(conditionalRequest.request.headers.get('If-None-Match')).toBe('"tree-v1"');
    conditionalRequest.flush(null, { status: 304, statusText: 'Not Modified', headers: { ETag: '"tree-v1"' } });
    await revalidation;

    expect(api.tree()).toBe(retainedNodes);
    expect(api.connectionState()).toBe('live');
    expect(api.repositoryTransition()).toBeNull();
  });

  it('keys retained tree ETags by both repository and requested path', async () => {
    const first = api.loadTree('default', false, 'src/first');
    http.expectOne('/api/repos/default/tree?path=src%2Ffirst')
      .flush({ nodes: [] }, { headers: { ETag: '"first-path"' } });
    await first;

    const otherPath = api.loadTree('default', false, 'src/second');
    const otherPathRequest = http.expectOne('/api/repos/default/tree?path=src%2Fsecond');
    expect(otherPathRequest.request.headers.has('If-None-Match')).toBeFalse();
    otherPathRequest.flush({ nodes: [] }, { headers: { ETag: '"second-path"' } });
    await otherPath;

    const firstAgain = api.loadTree('default', false, 'src/first');
    const firstAgainRequest = http.expectOne('/api/repos/default/tree?path=src%2Ffirst');
    expect(firstAgainRequest.request.headers.get('If-None-Match')).toBe('"first-path"');
    firstAgainRequest.flush(null, { status: 304, statusText: 'Not Modified' });
    await firstAgain;
  });

  it('replaces the tree snapshot and retained ETag after a changed 200 response', async () => {
    const oldNodes = [{ id: 'old', name: 'Old tree', level: 'project', path: '.', kinds: {}, children: [] }] satisfies TreeNode[];
    const freshNodes = [{ id: 'fresh', name: 'Fresh tree', level: 'project', path: '.', kinds: {}, children: [] }] satisfies TreeNode[];
    const initial = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=')
      .flush({ nodes: oldNodes }, { headers: { ETag: '"tree-v1"' } });
    await initial;

    const changed = api.loadTree('default', false);
    const changedRequest = http.expectOne('/api/repos/default/tree?path=');
    expect(changedRequest.request.headers.get('If-None-Match')).toBe('"tree-v1"');
    changedRequest.flush({ nodes: freshNodes }, { headers: { ETag: '"tree-v2"' } });
    await changed;
    expect(api.tree()).toBe(freshNodes);

    const verifyTag = api.loadTree('default', false);
    const verifyRequest = http.expectOne('/api/repos/default/tree?path=');
    expect(verifyRequest.request.headers.get('If-None-Match')).toBe('"tree-v2"');
    verifyRequest.flush(null, { status: 304, statusText: 'Not Modified' });
    await verifyTag;
  });

  it('conditionally revalidates dashboards, retaining 304 snapshots and replacing changed 200 responses', async () => {
    const retained = { generatedAt: '2026-08-11T10:00:00Z', metrics: { fileCount: 5_116 } } as ProjectDashboard;
    const fresh = { generatedAt: '2026-08-11T10:05:00Z', metrics: { fileCount: 5_117 } } as ProjectDashboard;
    const initial = api.loadProjectDashboard('default');
    const initialRequest = http.expectOne('/api/repos/default/project');
    expect(initialRequest.request.headers.has('If-None-Match')).toBeFalse();
    initialRequest.flush(retained, { headers: { ETag: '"project-v1"' } });
    await initial;

    api.repositoryTransition.set({ repositoryId: 'default', hasSnapshot: true });
    api.connectionState.set('connecting');
    const unchanged = api.loadProjectDashboard('default');
    const unchangedRequest = http.expectOne('/api/repos/default/project');
    expect(unchangedRequest.request.headers.get('If-None-Match')).toBe('"project-v1"');
    unchangedRequest.flush(null, { status: 304, statusText: 'Not Modified', headers: { ETag: '"project-v1"' } });
    await unchanged;
    expect(api.project()).toBe(retained);
    expect(api.connectionState()).toBe('live');
    expect(api.repositoryTransition()).toBeNull();

    const changed = api.loadProjectDashboard('default');
    const changedRequest = http.expectOne('/api/repos/default/project');
    expect(changedRequest.request.headers.get('If-None-Match')).toBe('"project-v1"');
    changedRequest.flush(fresh, { headers: { ETag: '"project-v2"' } });
    await changed;
    expect(api.project()).toBe(fresh);

    const verifyTag = api.loadProjectDashboard('default');
    const verifyRequest = http.expectOne('/api/repos/default/project');
    expect(verifyRequest.request.headers.get('If-None-Match')).toBe('"project-v2"');
    verifyRequest.flush(null, { status: 304, statusText: 'Not Modified' });
    await verifyTag;
  });

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

  it('renders a status-aware error instead of foreign content when a file lookup fails', async () => {
    await connect(api, http);

    const notFound = api.loadFile('missing.cs');
    http.expectOne('/api/repos/default/file?path=missing.cs')
      .flush({ detail: 'No review unit for missing.cs.' }, { status: 404, statusText: 'Not Found' });
    await notFound;

    expect(api.file()).toBeNull();
    expect(api.fileError()?.kind).toBe('out-of-scope');
    expect(api.fileError()?.status).toBe(404);
    expect(api.fileError()?.detail).toBe('No review unit for missing.cs.');
    expect(api.fileError()?.retryable).toBeFalse();
    expect(api.connectionState()).toBe('live');
    expect(api.preview()).toBeFalse();
  });

  it('separates denied access, oversized documents, and retryable server failures', async () => {
    await connect(api, http);

    for (const [status, kind, retryable] of [[401, 'unauthorized', false], [403, 'forbidden', false],
      [413, 'too-large', false], [503, 'unavailable', true]] as const) {
      const loading = api.loadFile(`case-${status}.cs`);
      http.expectOne(`/api/repos/default/file?path=case-${status}.cs`)
        .flush('failed', { status, statusText: 'Failed' });
      await loading;
      expect(api.file()).withContext(`status ${status}`).toBeNull();
      expect(api.fileError()?.kind).withContext(`status ${status}`).toBe(kind);
      expect(api.fileError()?.retryable).withContext(`status ${status}`).toBe(retryable);
    }
  });

  it('clears the error state when a later file request succeeds', async () => {
    await connect(api, http);

    const failing = api.loadFile('missing.cs');
    http.expectOne('/api/repos/default/file?path=missing.cs').flush('missing', { status: 404, statusText: 'Not Found' });
    await failing;
    expect(api.fileError()).not.toBeNull();

    const succeeding = api.loadFile('src/Program.cs');
    http.expectOne('/api/repos/default/file?path=src/Program.cs').flush({
      path: 'src/Program.cs', content: 'var app = 1;', metaDocuments: [], sizeBytes: 12, lineEnding: 'lf', encoding: 'utf-8',
    });
    await succeeding;

    expect(api.fileError()).toBeNull();
    expect(api.file()?.content).toBe('var app = 1;');
  });

  it('serves labelled preview fixtures only when the API cannot be reached at all', async () => {
    const treeLoading = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await treeLoading;

    expect(api.connectionState()).toBe('preview');
    expect(api.preview()).toBeTrue();
    expect(api.tree().length).withContext('preview tree fixture').toBeGreaterThan(0);

    const fileLoading = api.loadFile('src/QualityStudio.Api/Program.cs');
    http.expectOne('/api/repos/default/file?path=src/QualityStudio.Api/Program.cs')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await fileLoading;

    expect(api.fileError()).toBeNull();
    expect(api.file()?.content).toContain('WebApplication.CreateBuilder');
    expect(api.connectionLabel()).toBe('API offline, preview data');
  });

  it('keeps the tree empty and names the reason when a reachable API rejects it', async () => {
    const treeLoading = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=')
      .flush({ detail: 'Repository root is not readable.' }, { status: 500, statusText: 'Server Error' });
    await treeLoading;

    expect(api.tree()).toEqual([]);
    expect(api.connectionState()).toBe('offline');
    expect(api.preview()).toBeFalse();
    expect(api.connectionError()).toBe('Repository root is not readable.');
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

/** Brings the service to a live connection so file-level behaviour can be asserted on its own. */
async function connect(api: QualityApi, http: HttpTestingController): Promise<void> {
  const loading = api.loadTree();
  http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
  http.expectOne('/api/repos/default/scan').flush({ files: [], freshCount: 0, staleCount: 0, policyDriftCount: 0, missingCount: 0 });
  http.expectOne('/api/repos/default/inputs').flush({ kinds: {} });
  http.expectOne('/api/repos/default/guidelines').flush({ guidelines: [], catalogue: [], traces: [] });
  http.expectOne('/api/repos/default/risk?days=90').flush({ days: 90, currentCommit: null, rows: [], matrix: [] });
  await new Promise(resolve => setTimeout(resolve));
  http.expectOne('/api/repos/default/handover').flush({ targetConfigured: false, dryRun: true });
  await loading;
}
