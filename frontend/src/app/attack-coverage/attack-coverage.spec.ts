import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AttackCoverageCell, AttackCoverageMatrix, TreeNode } from '../contracts';
import { QualityApi } from '../quality-api';
import { AttackCoverage } from './attack-coverage';

function cell(overrides: Partial<AttackCoverageCell>): AttackCoverageCell {
  return {
    boundaryId: 'boundary-1', attackId: 'attack-1', verdict: 'pass', reason: 'Checked.', evidence: [],
    findingId: null, findingFingerprint: null, disagreement: false, deterministicOverride: false,
    needsHumanAttention: false, requiredJudgements: 1, independentJudgements: 1, confidence: 'high',
    checkedAt: '2026-09-01T10:00:00Z', ageDays: 2, stalenessReasons: [], provenance: [], history: [],
    ...overrides,
  };
}

const matrix: AttackCoverageMatrix = {
  schemaVersion: 1, catalogueVersion: '3', promptVersion: '2', promptHash: 'hash', generatedAt: '2026-09-01T10:00:00Z',
  scope: 'src/QualityStudio.Api',
  attacks: [{
    id: 'attack-1', version: '1', title: 'Injection', description: 'Attack description.',
    applicability: { boundaryKinds: ['http'] }, evidenceRequirements: [], severity: 'high',
    severityFrame: 'frame', deterministicRuleIds: [], deterministicPassConclusive: false, enabled: true,
  }],
  rows: [{
    boundary: { id: 'boundary-1', kind: 'http', direction: 'inbound', name: 'File route', transport: 'http', location: { path: 'src/QualityStudio.Api/Program.cs', line: 17 } },
    boundaryDefinitionHash: 'a', coveredCodeHash: 'b', codeChangeCount: 0, oldestVerdictAt: null,
    cells: [cell({ verdict: 'pass' }), cell({ attackId: 'attack-2', verdict: 'finding', needsHumanAttention: true, reason: 'Needs a decision.' })],
  }],
  cellCount: 2, notYetCheckedCount: 0, staleCount: 1, disagreementCount: 0,
};

describe('AttackCoverage', () => {
  let fixture: ComponentFixture<AttackCoverage>;
  let http: HttpTestingController;
  let api: QualityApi;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AttackCoverage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(QualityApi);
    fixture = TestBed.createComponent(AttackCoverage);
  });

  it('scopes the matrix to the API project when the tree contains it', async () => {
    const kinds = {} as TreeNode['kinds'];
    api.tree.set([{ id: 'root', name: 'Root', level: 'repository', path: '.', kinds, children: [
      { id: 'api', name: 'QualityStudio.Api', level: 'project', path: 'src/QualityStudio.Api', kinds, children: [] },
    ] }]);
    fixture.detectChanges();

    const request = http.expectOne(candidate => candidate.url.endsWith('/security/attack-coverage'));
    expect(request.request.params.get('path')).toBe('src/QualityStudio.Api');
    request.flush(matrix);
    await fixture.whenStable();

    expect(fixture.componentInstance.scope()).toBe('src/QualityStudio.Api');
  });

  it('falls back to the repository root when that project is absent', () => {
    api.tree.set([]);
    fixture.detectChanges();

    const request = http.expectOne(candidate => candidate.url.endsWith('/security/attack-coverage'));
    expect(request.request.params.get('path')).toBe('.');
    request.flush(matrix);
  });

  it('selects the cell that needs human attention first', async () => {
    api.tree.set([]);
    fixture.detectChanges();
    http.expectOne(candidate => candidate.url.endsWith('/security/attack-coverage')).flush(matrix);
    await fixture.whenStable();

    expect(fixture.componentInstance.selectedCell()?.attackId).toBe('attack-2');
  });

  it('shows the API error instead of an empty matrix and stays dismissible', async () => {
    api.tree.set([]);
    fixture.detectChanges();
    http.expectOne(candidate => candidate.url.endsWith('/security/attack-coverage'))
      .flush({ detail: 'Boundary derivation failed.' }, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.coverage-error')?.textContent)
      .toContain('Boundary derivation failed.');

    let closed = 0;
    fixture.componentInstance.closed.subscribe(() => closed++);
    (fixture.nativeElement.querySelector('.coverage-dialog') as HTMLElement)
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(closed).toBe(1);
  });

  it('reads an unchecked cell age as unchecked rather than zero days', () => {
    expect(fixture.componentInstance.age(cell({ ageDays: null }))).toBe('unchecked');
    expect(fixture.componentInstance.age(cell({ ageDays: 0.4 }))).toBe('<1d');
    expect(fixture.componentInstance.age(cell({ ageDays: 9.6 }))).toBe('9d');
  });
});
