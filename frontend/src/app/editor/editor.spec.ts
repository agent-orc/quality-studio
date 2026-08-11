import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { QualityApi } from '../quality-api';
import { Editor } from './editor';

describe('Editor', () => {
  let fixture: ComponentFixture<Editor>;
  let api: QualityApi;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Editor],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(Editor);
    fixture.componentRef.setInput('selectedPath', 'src/sample.ts');
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('viewportHeight', 600);
    api = TestBed.inject(QualityApi);
    api.file.set({ path: 'src/sample.ts', content: 'first\nsecond', metaDocuments: [], sizeBytes: 12, lineEnding: 'lf', encoding: 'utf-8' });
    fixture.detectChanges();
  });

  it('creates an inline thread and clears the composer draft', async () => {
    const mutate = spyOn(api, 'mutateThread').and.resolveTo({ id: 'thread-1' } as never);
    fixture.componentInstance.composingLine.set(2);
    fixture.componentInstance.setDraft('line:2', '  Please explain this branch.  ');

    await fixture.componentInstance.addThread(2);

    expect(mutate).toHaveBeenCalledWith(jasmine.objectContaining({
      path: 'src/sample.ts', kind: 'code', line: 2, body: 'Please explain this branch.',
    }));
    expect(fixture.componentInstance.composingLine()).toBeNull();
    expect(fixture.componentInstance.drafts()['line:2']).toBe('');
  });

  it('does not create an empty thread', async () => {
    const mutate = spyOn(api, 'mutateThread');
    fixture.componentInstance.setDraft('line:1', '   ');

    await fixture.componentInstance.addThread(1);

    expect(mutate).not.toHaveBeenCalled();
  });
});
