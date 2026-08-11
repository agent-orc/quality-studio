import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityApi, TreeNode } from '../quality-api';
import { ReviewActions } from './review-actions';

describe('ReviewActions', () => {
  let fixture: ComponentFixture<ReviewActions>;
  let api: QualityApi;
  const node = {
    id: 'file', name: 'sample.ts', level: 'file', path: 'src/sample.ts', kinds: {}, findingsCount: 0,
    findingCounts: { open: 0, accepted: 0, waived: 0, falsePositive: 0, resolved: 0 },
    reviewedAt: null, sizeBytes: 10, lineCount: 1, coverage: {}, excluded: [], children: [],
  } as unknown as TreeNode;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewActions],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ReviewActions);
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('activeKind', 'code');
    api = TestBed.inject(QualityApi);
    fixture.detectChanges();
  });

  it('starts a confirmed review with the preflight request', async () => {
    spyOn(window, 'confirm').and.returnValue(true);
    const estimate = spyOn(api, 'estimateReview').and.resolveTo({
      repositoryId: 'default', path: node.path, level: 'file', kind: 'code', model: null, cliType: 'codex',
      estimate: { files: 1, operations: 1, promptCharacters: 100, inputTokens: 25, outputTokens: 10,
        cost: null, currency: null, priceStatus: 'unavailable', historySamples: 0, method: 'fixture' },
      tokenCap: null, costCap: null,
    });
    const start = spyOn(api, 'startReview').and.resolveTo({} as never);

    await fixture.componentInstance.start();

    expect(estimate).toHaveBeenCalledWith(jasmine.objectContaining({ path: node.path, kind: 'code' }));
    expect(start).toHaveBeenCalled();
    expect(fixture.componentInstance.starting()).toBeFalse();
  });

  it('rejects a non-positive local cap before calling the API', async () => {
    fixture.componentInstance.capKind.set('tokens');
    fixture.componentInstance.capValue.set(0);
    const estimate = spyOn(api, 'estimateReview');

    await fixture.componentInstance.start();

    expect(estimate).not.toHaveBeenCalled();
    expect(api.reviewError()).toContain('positive per-run cap');
  });
});
