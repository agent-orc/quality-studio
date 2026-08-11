import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityApi, ReviewFinding } from '../quality-api';
import { ReviewPanel } from './review-panel';

describe('ReviewPanel', () => {
  let fixture: ComponentFixture<ReviewPanel>;
  let api: QualityApi;
  const finding = {
    id: 'finding-1', aspect: 'correctness', severity: 'high', title: 'Unsafe branch', description: 'Description',
    recommendation: 'Fix it', ruleId: 'QS001', fingerprint: `sha256:${'a'.repeat(64)}`,
    locations: [{ path: 'src/sample.ts' }],
  } as ReviewFinding;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewPanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ReviewPanel);
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('selectedPath', 'src/sample.ts');
    api = TestBed.inject(QualityApi);
    api.file.set({ path: 'src/sample.ts', content: 'source', metaDocuments: [], sizeBytes: 6, lineEnding: 'lf', encoding: 'utf-8' });
    fixture.detectChanges();
  });

  it('requires an audit reason before changing finding state', async () => {
    const mutate = spyOn(api, 'mutateFindingState');

    await fixture.componentInstance.setFindingState(finding, 'accepted');

    expect(mutate).not.toHaveBeenCalled();
    expect(fixture.componentInstance.stateStatus()).toBe('Author and reason are required.');
  });

  it('creates a handover task and exposes dry-run completion', async () => {
    spyOn(api, 'createTask').and.resolveTo({ dryRun: true, taskId: null, card: { title: 'Unsafe branch' } });

    await fixture.componentInstance.createTask(finding);

    expect(fixture.componentInstance.handoverStatus()['code:finding-1']).toBe('Dry run printed');
  });
});
