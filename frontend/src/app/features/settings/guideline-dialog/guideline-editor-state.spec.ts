import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { QualityApi } from '../../../core/api/quality-api';
import { ConfirmationState } from '../../../shared/dialog/confirmation-state';
import { GuidelineEditorState } from './guideline-editor-state';

describe('Guideline editor state', () => {
  it('defers deletion until confirmation and resets the empty editor afterwards', async () => {
    const deleted = jasmine.createSpy('deleteGuideline').and.resolveTo();
    TestBed.configureTestingModule({ providers: [GuidelineEditorState, ConfirmationState, {
      provide: QualityApi, useValue: { deleteGuideline: deleted, guidelines: signal([]) },
    }] });
    const state = TestBed.inject(GuidelineEditorState);
    const confirmations = TestBed.inject(ConfirmationState);
    state.editingGuidelineId.set('architecture');
    state.deleteGuideline();
    expect(deleted).not.toHaveBeenCalled();
    expect(confirmations.pending()?.heading).toContain('architecture');
    await confirmations.run();
    expect(deleted).toHaveBeenCalledOnceWith('architecture');
    expect(confirmations.pending()).toBeNull();
    expect(state.editingGuidelineId()).toBeNull();
  });
});
