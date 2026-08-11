import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { AttackCoverageCell, AttackCoverageMatrix, QualityApi } from '../quality-api';
import { AttackCoverage } from './attack-coverage';

describe('AttackCoverage', () => {
  let fixture: ComponentFixture<AttackCoverage>;
  const attackCoverage = signal<AttackCoverageMatrix | null>(null);
  const api = {
    tree: signal([{ id: 'api', name: 'API', path: 'src/QualityStudio.Api', level: 'module', kinds: {}, children: [] }]),
    attackCoverage,
    attackCoverageLoading: signal(false),
    attackCoverageError: signal(''),
    selectedRepositoryId: signal('default'),
    loadAttackCoverage: jasmine.createSpy('loadAttackCoverage'),
  };

  beforeEach(async () => {
    attackCoverage.set(null);
    api.attackCoverageLoading.set(false);
    api.attackCoverageError.set('');
    api.loadAttackCoverage.calls.reset();
    await TestBed.configureTestingModule({
      imports: [AttackCoverage],
      providers: [{ provide: QualityApi, useValue: api }],
    }).compileComponents();
  });

  it('loads the API scope and opens the first judgement needing human attention', async () => {
    const deferred = cell({ verdict: 'notYetChecked', needsHumanAttention: true, reason: 'Independent review required.' });
    const passing = cell({ verdict: 'pass', needsHumanAttention: false, reason: 'Mechanical control passed.' });
    const matrix = coverageMatrix([passing, deferred]);
    api.loadAttackCoverage.and.callFake(async () => {
      attackCoverage.set(matrix);
      return matrix;
    });

    fixture = TestBed.createComponent(AttackCoverage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.loadAttackCoverage).toHaveBeenCalledOnceWith('src/QualityStudio.Api');
    expect(fixture.componentInstance.selectedCell()).toBe(deferred);
    expect(fixture.nativeElement.textContent).toContain('Independent review required.');
    expect(fixture.nativeElement.textContent).toContain('1 not yet checked');
  });

  it('keeps request failures visible as an alert instead of rendering stale matrix data', async () => {
    api.loadAttackCoverage.and.callFake(async () => {
      api.attackCoverageError.set('Attack coverage service unavailable.');
      throw new Error('unavailable');
    });

    fixture = TestBed.createComponent(AttackCoverage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const alert = fixture.nativeElement.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Attack coverage service unavailable.');
    expect(fixture.nativeElement.querySelector('.coverage-table')).toBeNull();
  });
});

function cell(overrides: Partial<AttackCoverageCell>): AttackCoverageCell {
  return {
    boundaryId: 'boundary-1', attackId: 'QS-A01', verdict: 'notYetChecked', reason: 'Not checked.',
    evidence: [], findingId: null, findingFingerprint: null, disagreement: false,
    deterministicOverride: false, needsHumanAttention: false, requiredJudgements: 2,
    independentJudgements: 0, confidence: 'none', checkedAt: null, ageDays: null,
    stalenessReasons: [], provenance: [], history: [], ...overrides,
  };
}

function coverageMatrix(cells: AttackCoverageCell[]): AttackCoverageMatrix {
  return {
    schemaVersion: 1, catalogueVersion: '1.0.0', promptVersion: 'attack-coverage.v1', promptHash: 'sha256:test',
    generatedAt: '2026-08-11T08:00:00Z', scope: 'src/QualityStudio.Api',
    attacks: [{ id: 'QS-A01', version: '1.0.0', title: 'Authorization bypass', description: 'Check authorization.',
      applicability: { boundaryKinds: ['http'] }, evidenceRequirements: ['HTTP proof'], severity: 'high',
      severityFrame: 'Unauthorized access is high severity.', deterministicRuleIds: [],
      deterministicPassConclusive: false, enabled: true }],
    rows: [{ boundary: { id: 'boundary-1', kind: 'http', direction: 'inbound', name: 'POST /api/review',
      transport: 'HTTP', location: { path: 'src/QualityStudio.Api/Program.cs', line: 299 } },
      boundaryDefinitionHash: 'sha256:boundary', coveredCodeHash: 'sha256:code', codeChangeCount: 1,
      oldestVerdictAt: null, cells }],
    cellCount: cells.length,
    notYetCheckedCount: cells.filter(candidate => candidate.verdict === 'notYetChecked').length,
    staleCount: cells.filter(candidate => candidate.stalenessReasons.length > 0).length,
    disagreementCount: cells.filter(candidate => candidate.disagreement).length,
  };
}
