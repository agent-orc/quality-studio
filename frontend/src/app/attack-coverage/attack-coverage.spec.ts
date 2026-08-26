import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { AttackCoverageMatrix, QualityApi } from '../quality-api';
import { AttackCoverage } from './attack-coverage';

describe('AttackCoverage', () => {
  let fixture: ComponentFixture<AttackCoverage>;
  let component: AttackCoverage;
  const matrix = {
    schemaVersion: 1,
    catalogueVersion: 'catalogue-v1',
    promptVersion: 'prompt-v1',
    promptHash: 'sha256:prompt',
    generatedAt: '2026-08-26T00:00:00Z',
    scope: 'src/QualityStudio.Api',
    attacks: [{
      id: 'injection', version: '1', title: 'Injection', description: 'Untrusted input reaches a sink.',
      applicability: { boundaryKinds: ['http'] }, evidenceRequirements: ['A boundary test'],
      severity: 'high', severityFrame: 'Data integrity', deterministicRuleIds: [],
      deterministicPassConclusive: false, enabled: true,
    }],
    rows: [{
      boundary: { id: 'route', kind: 'http', direction: 'inbound', name: 'POST /api/review', transport: 'HTTP', location: { path: 'Program.cs', line: 300 } },
      boundaryDefinitionHash: 'sha256:boundary', coveredCodeHash: 'sha256:code', codeChangeCount: 2,
      oldestVerdictAt: null,
      cells: [{
        boundaryId: 'route', attackId: 'injection', verdict: 'notYetChecked', reason: 'No judgement yet.',
        evidence: [], findingId: null, findingFingerprint: null, disagreement: false,
        deterministicOverride: false, needsHumanAttention: true, requiredJudgements: 2,
        independentJudgements: 0, confidence: 'none', checkedAt: null, ageDays: null,
        stalenessReasons: [], provenance: [], history: [],
      }],
    }],
    cellCount: 1, notYetCheckedCount: 1, staleCount: 0, disagreementCount: 0,
  } satisfies AttackCoverageMatrix;
  const api = {
    tree: signal([{ id: 'api', name: 'API', path: 'src/QualityStudio.Api', level: 'module', kinds: {}, children: [] }]),
    attackCoverage: signal<AttackCoverageMatrix | null>(null),
    attackCoverageLoading: signal(false),
    attackCoverageError: signal(''),
    selectedRepositoryId: signal('default'),
    loadAttackCoverage: jasmine.createSpy('loadAttackCoverage'),
  };

  beforeEach(async () => {
    api.attackCoverage.set(null);
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
    fixture = TestBed.createComponent(AttackCoverage);
    component = fixture.componentInstance;
  });

  it('loads the API scope and focuses the first judgement needing human attention', async () => {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.loadAttackCoverage).toHaveBeenCalledWith('src/QualityStudio.Api');
    expect(component.selectedCell()?.attackId).toBe('injection');
    const cell = fixture.nativeElement.querySelector('[aria-label*="POST /api/review"]') as HTMLElement;
    expect(cell.textContent).toContain('—');
    expect(fixture.nativeElement.querySelector('.coverage-detail').textContent).toContain('No judgement yet.');
  });

  it('renders the API-owned failure state without inventing coverage', async () => {
    api.loadAttackCoverage.and.callFake(async () => {
      api.attackCoverageError.set('Attack coverage could not be loaded.');
      throw new Error('offline');
    });

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.selectedCell()).toBeNull();
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent)
      .toContain('Attack coverage could not be loaded.');
  });
});
