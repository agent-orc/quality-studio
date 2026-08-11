import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AttackCoverageMatrix, QualityApi } from '../quality-api';
import { AttackCoverage } from './attack-coverage';

describe('AttackCoverage', () => {
  let fixture: ComponentFixture<AttackCoverage>;
  let api: QualityApi;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AttackCoverage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(AttackCoverage);
    api = TestBed.inject(QualityApi);
  });

  it('selects the first cell requiring human attention', async () => {
    const cell = { attackId: 'A-1', needsHumanAttention: true, ageDays: null } as never;
    const matrix = { rows: [{ cells: [cell] }] } as unknown as AttackCoverageMatrix;
    spyOn(api, 'loadAttackCoverage').and.resolveTo(matrix);

    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.componentInstance.selectedCell()).toBe(cell);
    expect(fixture.componentInstance.age(cell)).toBe('unchecked');
  });

  it('renders checked ages without overstating partial days', () => {
    expect(fixture.componentInstance.age({ ageDays: 0.6 } as never)).toBe('<1d');
    expect(fixture.componentInstance.age({ ageDays: 3.9 } as never)).toBe('3d');
  });
});
