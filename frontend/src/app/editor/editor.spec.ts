import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FileDocument, QualityApi, ReviewFinding } from '../quality-api';
import { Editor } from './editor';
import { SyntaxHighlighting } from './syntax-highlighting';

const path = 'src/virtual.ts';

function finding(id: string, line: number, startColumn: number, endColumn: number): ReviewFinding {
  return {
    id,
    ruleId: `rule:${id}`,
    aspect: 'correctness',
    severity: 'medium',
    title: `Finding ${id}`,
    description: 'Problem',
    recommendation: 'Fix it',
    locations: [{ path, range: {
      start: { line, column: startColumn },
      end: { line, column: endColumn },
    } }],
  };
}

function document(content: string, findings: ReviewFinding[]): FileDocument {
  return {
    path,
    content,
    sizeBytes: new TextEncoder().encode(content).byteLength,
    lineEnding: 'lf',
    encoding: 'utf-8',
    metaDocuments: [{
      reviewedAt: '2026-08-11T00:00:00Z',
      kind: 'code',
      reviewer: { agent: 'test', model: 'test' },
      grade: { score: 80, band: 'B', rationale: 'Fixture' },
      summary: 'Fixture',
      findings,
    }],
  };
}

describe('Editor finding spans', () => {
  let fixture: ComponentFixture<Editor>;
  let file: WritableSignal<FileDocument | null>;

  beforeEach(async () => {
    file = signal<FileDocument | null>(null);
    const api = {
      file,
      focusedThreadId: signal<string | null>(null),
      loading: signal(false),
      risk: signal({ days: 90, currentCommit: null, rows: [], matrix: [] }),
      reviewRuns: signal([]),
      reviewError: signal(''),
      connected: signal(false),
    };
    await TestBed.configureTestingModule({
      imports: [Editor],
      providers: [
        { provide: QualityApi, useValue: api },
        { provide: SyntaxHighlighting, useValue: { highlight: () => () => undefined } },
      ],
    }).compileComponents();
  });

  function createEditor(): Editor {
    fixture = TestBed.createComponent(Editor);
    fixture.componentRef.setInput('selectedPath', path);
    fixture.componentRef.setInput('activeKind', 'code');
    fixture.componentRef.setInput('viewportHeight', 220);
    fixture.componentRef.setInput('selectedFinding', null);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('scrolls a card selection to the exact virtualized anchor', async () => {
    const selected = finding('deep', 900, 2, 4);
    file.set(document(Array.from({ length: 1_000 }, (_, index) => `line ${index + 1}`).join('\n'), [selected]));
    const editor = createEditor();

    fixture.componentRef.setInput('selectedFinding', selected);
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();

    expect(editor.codeScrollTop()).toBe(899 * editor.lineHeight - editor.lineHeight * 4);
    const row = editor.visibleRows().find(candidate => candidate.kind === 'code' && candidate.number === 900);
    expect(row).toBeDefined();
    if (!row || row.kind !== 'code') return;
    expect(row.segments.filter(segment => segment.selected).map(segment => segment.text).join('')).toBe('ine');
  });

  it('opens a keyboard-accessible chooser for overlapping exact spans', () => {
    const left = finding('left', 1, 2, 5);
    const right = finding('right', 1, 4, 7);
    file.set(document('abcdefgh', [left, right]));
    createEditor();
    fixture.componentRef.setInput('selectedFinding', right);
    fixture.detectChanges();

    const overlap = fixture.nativeElement.querySelector('.finding-span-overlap') as HTMLElement;
    expect(overlap.textContent).toBe('de');
    expect(overlap.getAttribute('role')).toBe('button');
    expect(overlap.getAttribute('aria-label')).toContain('2 overlapping findings');
    overlap.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();

    const chooser = fixture.nativeElement.querySelector('.finding-overlap-chooser') as HTMLElement;
    expect(chooser.getAttribute('role')).toBe('menu');
    const choices = [...chooser.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')];
    expect(choices.length).toBe(2);
    let emitted: ReviewFinding | undefined;
    fixture.componentInstance.findingSelect.subscribe(value => emitted = value);
    choices[1].click();
    expect(emitted?.id).toBe('right');
  });
});
