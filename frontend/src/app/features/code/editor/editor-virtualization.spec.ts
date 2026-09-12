import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { FileDocument, FileError, ReviewMetaDocument, ReviewThread } from '../../../core/models/contracts';
import { QualityApi } from '../../../core/api/quality-api';
import { Editor } from './editor';
import { SyntaxHighlighting } from './syntax-highlighting';

const LINE_COUNT = 4_000;

function meta(threads: ReviewThread[] = []): ReviewMetaDocument {
  return {
    reviewedAt: '2026-09-01T08:00:00Z', kind: 'code', reviewer: { agent: 'reviewer', model: 'model' },
    grade: { score: 80, band: 'B', rationale: 'Test.' }, summary: 'Test.', findings: [], threads,
  };
}

function thread(line: number): ReviewThread {
  return {
    id: `thread-${line}`,
    anchor: { path: 'src/A.cs', fingerprint: 'sha256:a', contextHash: 'hash', lastKnownRange: { start: { line, column: 1 }, end: { line, column: 1 } } },
    status: 'open',
    entries: [{ id: 'e1', author: { kind: 'human', name: 'Reviewer' }, createdAt: '2026-09-01T08:00:00Z', body: 'Comment.' }],
  };
}

describe('Editor virtualization', () => {
  let fixture: ComponentFixture<Editor>;
  let component: Editor;
  const initialFile: FileDocument = {
    path: 'src/A.cs',
    content: Array.from({ length: LINE_COUNT }, (_, index) => `line ${index + 1}`).join('\n'),
    metaDocuments: [meta()],
    sizeBytes: 48_000,
    lineEnding: 'lf',
    encoding: 'utf-8',
  };
  const file = signal<FileDocument>(initialFile);
  const api = {
    file,
    fileError: signal<FileError | null>(null),
    preview: signal(false),
    loading: signal(false),
    risk: signal({ rows: [], matrix: [], days: 90, currentCommit: null }),
    focusedThreadId: signal<string | null>(null),
    mutateThread: jasmine.createSpy('mutateThread'),
  };
  const node = { id: 'a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds: { code: { direct: 'fresh' } }, children: [] };

  beforeEach(async () => {
    // Each test starts from the same document: the suite runs in random order.
    file.set(initialFile);
    await TestBed.configureTestingModule({
      imports: [Editor],
      providers: [
        { provide: QualityApi, useValue: api },
        { provide: SyntaxHighlighting, useValue: { highlight: () => () => undefined } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(Editor);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('selectedPath', 'src/A.cs');
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('selectedNode', node);
    fixture.componentRef.setInput('viewportHeight', 800);
    fixture.detectChanges();
  });

  it('lays out every line but renders only an overscanned window of them', () => {
    expect(component.layoutRows().length).toBe(LINE_COUNT);
    expect(component.codeSpaceHeight()).toBe(LINE_COUNT * component.lineHeight);

    const visible = component.visibleRows().length;
    expect(visible).toBeGreaterThan(0);
    expect(visible).toBeLessThan(120);
    expect(fixture.nativeElement.querySelectorAll('.code-line').length).toBe(visible);
  });

  it('moves the window with the scroll position without changing the laid-out height', () => {
    const top = component.visibleRows()[0];
    component.codeScrollTop.set(20_000);
    fixture.detectChanges();

    const scrolled = component.visibleRows();
    expect(scrolled[0]).not.toBe(top);
    expect(scrolled.every(row => row.top + row.height >= 20_000 - 240)).toBeTrue();
    expect(component.codeSpaceHeight()).toBe(LINE_COUNT * component.lineHeight);
    expect(component.topVisibleLine()).toBeGreaterThan(800);
  });

  it('gives an expanded thread its own height and pushes the lines below it down', () => {
    file.update(current => ({ ...current, metaDocuments: [meta([thread(3)])] }));
    fixture.detectChanges();

    const collapsed = component.layoutRows().find(row => row.kind === 'thread')!;
    const lineAfter = component.layoutRows().find(row => row.kind === 'code' && row.number === 4)!;
    expect(collapsed.height).toBe(40);
    expect(lineAfter.top).toBe(collapsed.top + collapsed.height);

    component.toggleThread(thread(3));
    fixture.detectChanges();

    const expanded = component.layoutRows().find(row => row.kind === 'thread')!;
    expect(expanded.height).toBeGreaterThan(collapsed.height);
    expect(component.codeSpaceHeight()).toBeGreaterThan(LINE_COUNT * component.lineHeight);
  });

  it('stays plain above the large-file limit instead of tokenizing it', () => {
    file.update(current => ({ ...current, sizeBytes: 400 * 1024 }));
    fixture.detectChanges();

    expect(component.largeFileMode()).toBeTrue();
    expect(component.syntaxState()).toBe('large');
    expect(component.tokensForLine(1, 'line 1')).toEqual([{ text: 'line 1', kind: 'plain' }]);
  });
});
