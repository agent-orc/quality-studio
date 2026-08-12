import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { AttackCoverageMatrix, QualityApi, TreeNode } from '../quality-api';
import { AttackCoverage } from './attack-coverage';

describe('AttackCoverage', () => {
  let fixture: ComponentFixture<AttackCoverage>;
  const tree: TreeNode[] = [{
    id: 'api', name: 'QualityStudio.Api', level: 'project', path: 'src/QualityStudio.Api',
    kinds: {}, children: [],
  }];
  const matrix: AttackCoverageMatrix = {
    schemaVersion: 1,
    catalogueVersion: 'catalogue-v1',
    promptVersion: 'prompt-v1',
    promptHash: `sha256:${'a'.repeat(64)}`,
    generatedAt: '2026-08-12T08:00:00Z',
    scope: 'src/QualityStudio.Api',
    attacks: [{
      id: 'api-authz', version: '1', title: 'Authorization bypass', description: 'Checks repository authorization.',
      applicability: { boundaryKinds: ['http'] }, evidenceRequirements: ['HTTP contract test'],
      severity: 'high', severityFrame: 'Cross-tenant access.', deterministicRuleIds: [],
      deterministicPassConclusive: false, enabled: true,
    }],
    rows: [{
      boundary: {
        id: 'boundary-1', kind: 'http', direction: 'inbound', name: 'GET /api/repos', transport: 'HTTP',
        location: { path: 'src/QualityStudio.Api/Program.cs', line: 212 },
      },
      boundaryDefinitionHash: `sha256:${'b'.repeat(64)}`,
      coveredCodeHash: `sha256:${'c'.repeat(64)}`,
      codeChangeCount: 2,
      oldestVerdictAt: '2026-08-01T08:00:00Z',
      cells: [{
        boundaryId: 'boundary-1', attackId: 'api-authz', verdict: 'finding',
        reason: 'Independent judgements disagree about the authorization proof.',
        evidence: [{ kind: 'test', reference: 'ApiSecurityTests.cs', summary: 'Tenant isolation contract.' }],
        findingId: 'authz-gap', findingFingerprint: null, disagreement: true,
        deterministicOverride: false, needsHumanAttention: true, requiredJudgements: 2,
        independentJudgements: 1, confidence: 'medium', checkedAt: '2026-08-01T08:00:00Z', ageDays: 11.5,
        stalenessReasons: ['codeChanged'], provenance: [], history: [],
      }],
    }],
    cellCount: 1, notYetCheckedCount: 0, staleCount: 1, disagreementCount: 1,
  };
  const api = {
    tree: signal(tree), attackCoverage: signal<AttackCoverageMatrix | null>(null),
    attackCoverageLoading: signal(false), attackCoverageError: signal(''),
    selectedRepositoryId: signal('default'), loadAttackCoverage: jasmine.createSpy('loadAttackCoverage'),
  };

  beforeEach(async () => {
    api.attackCoverage.set(null);
    api.attackCoverageLoading.set(false);
    api.attackCoverageError.set('');
    api.loadAttackCoverage.calls.reset();
    api.loadAttackCoverage.and.callFake(async () => {
      api.attackCoverage.set(matrix);
      return matrix;
    });
    await TestBed.configureTestingModule({
      imports: [AttackCoverage],
      providers: [{ provide: QualityApi, useValue: api }],
    }).compileComponents();
  });

  it('loads the API scope and prioritizes a stale disagreement for human judgement', async () => {
    fixture = TestBed.createComponent(AttackCoverage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.loadAttackCoverage).toHaveBeenCalledOnceWith('src/QualityStudio.Api');
    expect(fixture.componentInstance.selectedCell()?.attackId).toBe('api-authz');
    const dialog = fixture.nativeElement.querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.textContent).toContain('1 stale');
    expect(dialog.textContent).toContain('1 disagreements');
    expect(dialog.textContent).toContain('Independent judgements disagree');
    expect(dialog.textContent).toContain('11d');
    expect(dialog.textContent).toContain('ApiSecurityTests.cs');
    expect(dialog.querySelector('.disagreement-cell')).not.toBeNull();
  });

  it('keeps the API-owned failure visible when matrix loading fails', async () => {
    api.loadAttackCoverage.and.callFake(async () => {
      api.attackCoverageError.set('Attack coverage could not be loaded.');
      throw new Error('request failed');
    });
    fixture = TestBed.createComponent(AttackCoverage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Attack coverage could not be loaded.');
    expect(fixture.componentInstance.selectedCell()).toBeNull();
  });
});
