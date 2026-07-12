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
    http.expectOne('/api/tree?path=').flush({ nodes: [] satisfies TreeNode[] });
    http.expectOne('/api/scan').flush({ files: [], freshCount: 0, staleCount: 0, missingCount: 0 });
    http.expectOne('/api/security/scan').flush({
      verdict: 'pass',
      available: true,
      scanner: 'gitleaks',
      version: '8.24.2',
      mode: 'repository',
      range: null,
      configPath: null,
      baselinePath: null,
      scannedAt: '2026-07-11T16:20:00.000Z',
      filesScanned: 0,
      newFindings: 0,
      acceptedFindings: 0,
      blockFindings: 0,
      warnFindings: 0,
      cleanFiles: 0,
      unavailableReason: null,
      provenance: {
        scanner: 'gitleaks',
        version: '8.24.2',
        mode: 'repository',
        range: null,
        configPath: null,
        baselinePath: null,
        scannedAt: '2026-07-11T16:20:00.000Z',
      },
      counts: {
        filesScanned: 0,
        newFindings: 0,
        acceptedFindings: 0,
        blockFindings: 0,
        warnFindings: 0,
        cleanFiles: 0,
      },
      findings: [],
    });
    http.expectOne('/api/inputs').flush({ kinds: { code: input } });

    await new Promise(resolve => setTimeout(resolve));
    http.expectOne('/api/handover').flush({ targetConfigured: false, dryRun: true });
    await loading;

    expect(api.connected()).toBeTrue();
    expect(api.inputs().code).toEqual(input);
    expect(api.inputs().code?.inputs[0].id).toBe('code-style');
  });
});
