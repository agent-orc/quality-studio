import { TestBed } from '@angular/core/testing';
import { QualityApi } from '../../../core/api/quality-api';
import { RepositoryRegistration } from '../../../core/models/contracts';
import { RepositoryEditorState } from './repository-editor-state';

describe('Repository editor state', () => {
  let state: RepositoryEditorState;
  let api: jasmine.SpyObj<QualityApi>;

  beforeEach(() => {
    api = jasmine.createSpyObj<QualityApi>('QualityApi', ['createRepository', 'updateRepository', 'errorMessage']);
    TestBed.configureTestingModule({ providers: [RepositoryEditorState, { provide: QualityApi, useValue: api }] });
    state = TestBed.inject(RepositoryEditorState);
  });

  it('keeps token and cost budgets mutually exclusive and validates compact token input', () => {
    state.setDefaultCostCap(2);
    expect(state.repositoryForm.defaultReviewTokenCap).toBeNull();
    state.setDefaultTokenCap('0.1M');
    expect(state.repositoryForm.defaultReviewTokenCap).toBe(100000);
    expect(state.repositoryForm.defaultReviewCostCap).toBeNull();
    state.normalizeDefaultTokenCap();
    expect(state.repositoryTokenCapText).toBe('100k');
    state.setDefaultTokenCap('2B');
    expect(state.repositoryTokenCapError()).toContain('1B');
    state.setDefaultCostCap(3);
    expect(state.repositoryTokenCapError()).toBe('');
    expect(state.repositoryTokenCapText).toBe('');
  });

  it('edits review kinds without changing the registered repository before saving', () => {
    const repository = { ...state.repositoryForm, id: 'example', archived: false } as RepositoryRegistration;
    state.editRepository(repository);
    state.toggleReviewKind('security', false);
    expect(repository.enabledReviewKinds).toContain('security');
    expect(state.repositoryForm.enabledReviewKinds).not.toContain('security');
  });

  it('keeps the editor open with its entered data when saving fails', async () => {
    state.repositoryDialogOpen.set(true);
    state.repositoryForm.displayName = 'Working copy';
    api.createRepository.and.rejectWith(new Error('Unavailable'));
    api.errorMessage.and.returnValue('API unavailable');
    const navigate = jasmine.createSpy('navigate').and.resolveTo();
    await state.saveRepository(navigate);
    expect(state.repositoryDialogOpen()).toBeTrue();
    expect(state.repositoryForm.displayName).toBe('Working copy');
    expect(state.repositoryError()).toBe('API unavailable');
    expect(state.repositorySaving()).toBeFalse();
    expect(navigate).not.toHaveBeenCalled();
  });
});
