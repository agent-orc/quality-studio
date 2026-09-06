import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { QualityApi } from '../quality-api';
import { FileError, ReviewFinding } from '../contracts';
import { Editor } from './editor';
import { SyntaxHighlighting } from './syntax-highlighting';

describe('Editor finding navigation', () => {
  let fixture: ComponentFixture<Editor>;
  let component: Editor;
  const finding: ReviewFinding = {
    id: 'range', fingerprint: `sha256:${'d'.repeat(64)}`, ruleId: 'range.rule', aspect: 'correctness', severity: 'high',
    title: 'Selected range', description: 'Range description.', recommendation: 'Fix range.',
    locations: [{ path: 'src/A.cs', range: { start: { line: 8, column: 1 }, end: { line: 10, column: 4 } } }],
  };
  const api = {
    file: signal({
      path: 'src/A.cs', content: Array.from({ length: 30 }, (_, index) => `line ${index + 1}`).join('\n'),
      metaDocuments: [{ reviewedAt: '2026-08-11T08:00:00Z', kind: 'code', reviewer: { agent: 'reviewer', model: 'model' },
        grade: { score: 80, band: 'B', rationale: 'Test.' }, summary: 'Test.', findings: [finding] }],
      sizeBytes: 240, lineEnding: 'lf' as const, encoding: 'utf-8' as const,
    }),
    fileError: signal<FileError | null>(null), preview: signal(false),
    loading: signal(false), risk: signal({ rows: [], matrix: [] }), focusedThreadId: signal(null),
    mutateThread: jasmine.createSpy('mutateThread'),
  };
  const node = { id: 'a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds: { code: { direct: 'fresh' } }, children: [] };

  beforeEach(async () => {
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
    fixture.componentRef.setInput('selectedFinding', finding);
    fixture.componentRef.setInput('selectedLocationIndex', 0);
    fixture.componentRef.setInput('viewportHeight', 300);
    fixture.detectChanges();
  });

  it('centres and highlights the authoritative range with a focusable fingerprint marker', async () => {
    await fixture.whenStable();
    fixture.detectChanges();
    expect(component.isSelectedLine(7)).toBeFalse();
    expect(component.isSelectedLine(8)).toBeTrue();
    expect(component.isSelectedLine(10)).toBeTrue();
    expect(component.isSelectedLine(11)).toBeFalse();
    expect(component.codeScrollTop()).toBeGreaterThan(0);
    expect(fixture.nativeElement.querySelectorAll('.code-line.selected-range').length).toBe(3);
    expect(fixture.nativeElement.querySelector('[data-finding-fingerprint]')?.getAttribute('data-finding-fingerprint')).toBe(finding.fingerprint);
  });

  it('does not claim an authoritative range when the review is stale', () => {
    fixture.componentRef.setInput('selectedNode', { ...node, kinds: { code: { direct: 'stale' } } });
    fixture.detectChanges();
    expect(component.selectedLocation()).toBeNull();
    expect(component.isSelectedLine(8)).toBeFalse();
    expect(fixture.nativeElement.querySelectorAll('.code-line.selected-range').length).toBe(0);
  });

  it('selects a finding on click and keyboard focus, but never on hover', async () => {
    await fixture.whenStable();
    fixture.detectChanges();
    const selected: string[] = [];
    component.findingSelect.subscribe(emitted => selected.push(emitted.id));
    const marker = fixture.nativeElement.querySelector('.finding-marker') as HTMLButtonElement;

    marker.dispatchEvent(new MouseEvent('mouseenter', { bubbles: false }));
    marker.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
    expect(selected).withContext('hover only highlights').toEqual([]);

    marker.click();
    expect(selected).toEqual(['range']);

    marker.dispatchEvent(new FocusEvent('focus'));
    expect(selected).toEqual(['range', 'range']);
  });

  it('offers a retry for a retryable file failure and never renders code for it', () => {
    api.fileError.set({
      path: 'src/A.cs', kind: 'unavailable', status: 503, title: 'File could not be loaded',
      detail: 'The API did not deliver this document.', retryable: true,
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.code-line').length).toBe(0);
    expect(fixture.nativeElement.querySelector('.file-error b')?.textContent).toContain('File could not be loaded');
    expect(fixture.nativeElement.querySelector('.file-error button')).not.toBeNull();

    api.fileError.set(null);
    fixture.detectChanges();
  });

  it('labels preview content and hides comment mutations while the API is unreachable', () => {
    api.preview.set(true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.preview-banner')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.comment-add').length).toBe(0);

    api.preview.set(false);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.comment-add').length).toBeGreaterThan(0);
  });
});
