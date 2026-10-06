import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { QualityApi } from '../../../core/api/quality-api';
import { AnalyzerFileView, FileError, ReviewFinding } from '../../../core/models/contracts';
import { Editor } from './editor';
import { SyntaxHighlighting } from './syntax-highlighting';

describe('Editor analyzer findings', () => {
  let fixture: ComponentFixture<Editor>;
  let component: Editor;
  const analyzerFinding: ReviewFinding = {
    id: 'microsoft-codeanalysis-ca2016-000000000001', fingerprint: `sha256:${'a'.repeat(64)}`, ruleId: 'CA2016',
    aspect: 'analyzer', severity: 'medium', title: 'Forward the CancellationToken parameter to methods',
    description: 'Forward the cancellationToken parameter to ReadAsync.', recommendation: 'Pass the token.',
    locations: [{ path: 'src/A.cs', range: { start: { line: 21, column: 5 }, end: { line: 21, column: 30 } } }],
    source: { kind: 'deterministic', sensorId: 'roslyn', producer: 'Microsoft.CodeAnalysis' },
  } as ReviewFinding;
  const analyzers: AnalyzerFileView = {
    sensors: [
      { sensorId: 'roslyn', available: true, unavailableReason: null, scannedAt: '2026-09-28T08:00:00Z', scope: 'repository', target: '.',
        findings: 1, suppressedFindings: 2, toolVersions: {}, lastAttempt: { available: true, unavailableReason: null, scannedAt: '2026-09-28T08:00:00Z', scope: 'repository', target: '.' } },
      { sensorId: 'tsc', available: false, unavailableReason: null, scannedAt: null, scope: null, target: null,
        findings: 0, suppressedFindings: 0, toolVersions: {}, lastAttempt: { available: false, unavailableReason: 'typescript/bin/tsc is not installed', scannedAt: '2026-09-28T08:00:00Z', scope: 'repository', target: '.' } },
    ],
    findings: [{ sensorId: 'roslyn', finding: analyzerFinding, catalogueRules: [
      { id: 'QS-CS-003', title: 'Propagate CancellationToken; never write async void', technology: 'dotnet', enabled: true, severity: 'high' },
    ] }],
  };
  const api = {
    file: signal({
      path: 'src/A.cs', content: Array.from({ length: 60 }, (_, index) => `line ${index + 1}`).join('\n'),
      metaDocuments: [], sizeBytes: 480, lineEnding: 'lf' as const, encoding: 'utf-8' as const, analyzers,
    }),
    fileError: signal<FileError | null>(null), preview: signal(false),
    loading: signal(false), risk: signal({ rows: [], matrix: [] }), focusedThreadId: signal(null),
    mutateThread: jasmine.createSpy('mutateThread'),
  };
  const node = { id: 'a', name: 'A.cs', path: 'src/A.cs', level: 'file', kinds: {}, children: [] };

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
    fixture.componentRef.setInput('viewportHeight', 300);
    fixture.detectChanges();
  });

  it('lists persisted analyzer findings with the catalogue rule they enforce, without a review', () => {
    const strip: HTMLElement = fixture.nativeElement.querySelector('.analyzer-strip');
    expect(strip).not.toBeNull();
    expect(strip.textContent).toContain('1 finding in this file');
    expect(strip.textContent).toContain('roslyn: 1 finding, 2 suppressed at source');
    expect(strip.textContent).toContain('tsc: unavailable');
    expect(strip.querySelector('.analyzer-list code')?.textContent).toContain('CA2016');
    expect(strip.querySelector('.catalogue-rule')?.textContent?.trim()).toBe('QS-CS-003');
  });

  it('marks the analyzer lines in the gutter and reveals a finding on click', () => {
    expect(component.analyzerByLine().get(21)?.length).toBe(1);
    expect(component.analyzerTitle(component.analyzerByLine().get(21)!)).toContain('CA2016 → QS-CS-003');
    (fixture.nativeElement.querySelector('.analyzer-list button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(component.codeScrollTop()).toBeGreaterThan(0);
    expect(fixture.nativeElement.querySelector('.analyzer-marker.medium')).not.toBeNull();
  });
});
