import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QualityApi, RepositoryRegistration, ResolvedInputs, TreeNode } from './quality-api';

const registration = (id: string): RepositoryRegistration => ({
  id, displayName: id, rootPath: `/repos/${id}`, globalInputsDirectory: null, inputBudgetCharacters: 12000,
  enabledReviewKinds: ['code', 'security', 'performance'], archived: false,
  defaultReviewTokenCap: 100000, defaultReviewCostCap: null,
});

describe('QualityApi', () => {
  let api: QualityApi;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.removeItem('qs-last-repository');
    TestBed.configureTestingModule({
      providers: [QualityApi, provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(QualityApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('qs-last-repository');
  });

  it('selects the preferred repository restored by the application session', async () => {
    const loading = api.loadRepositories('agent-studio');
    http.expectOne('/api/repos').flush({
      repositories: [
        { id: 'default', displayName: 'Quality Studio', rootPath: '/work/quality-studio', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code'], archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null },
        { id: 'agent-studio', displayName: 'Agent Studio', rootPath: 'C:\\Projects\\agent-taskboard-devspace\\agent-taskboard', globalInputsDirectory: null, inputBudgetCharacters: 12000, enabledReviewKinds: ['code'], archived: false, defaultReviewTokenCap: 100000, defaultReviewCostCap: null },
      ],
      defaultRepositoryId: 'default',
    });
    await loading;

    expect(api.selectedRepositoryId()).toBe('agent-studio');
    expect(api.selectedRepository()?.displayName).toBe('Agent Studio');
  });

  it('shows API-down state and clears it after a successful retry request', async () => {
    const repositories = api.loadRepositories();
    http.expectOne('/api/repos').error(new ProgressEvent('error'));
    await repositories;

    expect(api.connectionState()).toBe('offline');

    const retry = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
    await retry;

    expect(api.connectionState()).toBe('live');
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

  it('restores the last active repository on the first load and remembers a switch', async () => {
    localStorage.setItem('qs-last-repository', 'beta');
    const loading = api.loadRepositories(null);
    http.expectOne('/api/repos').flush({
      repositories: [registration('alpha'), registration('beta')],
      defaultRepositoryId: 'alpha',
    });
    await loading;
    expect(api.selectedRepositoryId()).toBe('beta');

    // A later registry reload must keep the operator where they are, not jump back.
    const reloading = api.loadRepositories(null);
    http.expectOne('/api/repos').flush({
      repositories: [registration('alpha'), registration('beta')],
      defaultRepositoryId: 'alpha',
    });
    await reloading;
    expect(api.selectedRepositoryId()).toBe('beta');
    expect(localStorage.getItem('qs-last-repository')).toBe('beta');
  });

  it('falls back to the default repository when the remembered one is gone', async () => {
    localStorage.setItem('qs-last-repository', 'retired');
    const loading = api.loadRepositories(null);
    http.expectOne('/api/repos').flush({ repositories: [registration('alpha')], defaultRepositoryId: 'alpha' });
    await loading;

    expect(api.selectedRepositoryId()).toBe('alpha');
    expect(localStorage.getItem('qs-last-repository')).toBe('alpha');
  });

  it('reports the API as offline when no response arrives and recovers on retry', async () => {
    const failing = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await failing;
    expect(api.connectionState()).toBe('offline');
    expect(api.connectionLabel()).toBe('API offline');

    const recovering = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
    await recovering;
    expect(api.connectionState()).toBe('live');
  });

  it('reports an unreachable API when a reverse proxy returns a gateway error', async () => {
    const failing = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=').flush('upstream unavailable', {
      status: 502,
      statusText: 'Bad Gateway',
    });
    await failing;

    expect(api.connectionState()).toBe('offline');
    expect(api.connectionLabel()).toBe('API offline');
  });

  it('keeps the visible registry and selection when a retry finds the API still unreachable', async () => {
    const loading = api.loadRepositories(null);
    http.expectOne('/api/repos').flush({
      repositories: [registration('alpha'), registration('beta')],
      defaultRepositoryId: 'alpha',
    });
    await loading;
    api.selectedRepositoryId.set('beta');

    const retrying = api.loadRepositories('beta');
    http.expectOne('/api/repos').error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await retrying;

    expect(api.connectionState()).toBe('offline');
    expect(api.selectedRepositoryId()).toBe('beta');
    expect(api.repositories().map(repository => repository.id)).toEqual(['alpha', 'beta']);
  });

  it('falls back to the legacy default when a pre-registry server answers', async () => {
    const loading = api.loadRepositories(null);
    http.expectOne('/api/repos').flush('missing', { status: 404, statusText: 'Not Found' });
    await loading;

    expect(api.selectedRepositoryId()).toBe('default');
    expect(api.repositories().map(repository => repository.id)).toEqual(['default']);
    // The legacy server has no registry routes, so later calls must use the unprefixed base.
    const treeLoading = api.loadTree('default', false);
    http.expectOne('/api/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
    await treeLoading;
  });

  it('keeps the preview state when the API answers with a server error', async () => {
    const failing = api.loadTree('default', false);
    http.expectOne('/api/repos/default/tree?path=').flush('boom', { status: 500, statusText: 'Server Error' });
    await failing;

    expect(api.connectionState()).toBe('preview');
    expect(api.connectionLabel()).toBe('API offline, preview data');
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
