import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { QualityApi, ReviewFinding } from '../quality-api';
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
    loading: signal(false), risk: signal({ rows: [], matrix: [] }), focusedThreadId: signal(null),
    mutateThread: jasmine.createSpy('mutateThread'),
  };
  const node = { id: 'a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds: { code: { direct: 'fresh' } }, children: [] };

  beforeEach(async () => {
    api.file.update(value => ({ ...value, metaDocuments: [{ ...value.metaDocuments[0], findings: [finding] }] }));
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

  it('opens an accessible chooser for overlapping exact spans and selects one finding', async () => {
    const overlapping: ReviewFinding = {
      ...finding, id: 'overlap', fingerprint: `sha256:${'e'.repeat(64)}`, title: 'Overlapping range',
      locations: [{ path: 'src/A.cs', range: { start: { line: 8, column: 2 }, end: { line: 8, column: 4 } } }],
    };
    api.file.update(value => ({
      ...value, metaDocuments: [{ ...value.metaDocuments[0], findings: [finding, overlapping] }],
    }));
    const selected = jasmine.createSpy('selected');
    component.findingSelect.subscribe(selected);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const segment: HTMLButtonElement = fixture.nativeElement.querySelector('.finding-segment.overlap');
    expect(segment).toBeTruthy();
    expect(segment.getAttribute('aria-label')).toContain('2 overlapping findings');
    segment.click();
    fixture.detectChanges();

    const choices: NodeListOf<HTMLButtonElement> = fixture.nativeElement.querySelectorAll('.finding-overlap-chooser [role="menuitem"]');
    expect(choices.length).toBe(2);
    choices[1].click();
    expect(selected).toHaveBeenCalledWith(overlapping);
    expect(component.overlapChooser()).toBeNull();
  });
});
