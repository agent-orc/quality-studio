import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QualityApi, ResolvedInputs, TreeNode } from './quality-api';

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
    http.expectOne('/api/repos/default/findings/suppressions').flush({ schemaVersion: 1, revision: 0, rules: [] });

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
    http.expectOne('/api/repos/default/findings/suppressions').flush({ schemaVersion: 1, revision: 0, rules: [] });
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
