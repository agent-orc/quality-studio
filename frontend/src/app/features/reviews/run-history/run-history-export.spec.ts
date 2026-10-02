import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, fakeAsync, flushMicrotasks, TestBed, tick } from '@angular/core/testing';

import { ApiAccess } from '../../../core/api/api-access';
import { apiInterceptor } from '../../../core/api/api-interceptor';
import { QualityApi } from '../../../core/api/quality-api';
import { QualityRunReport, ReviewRun } from '../../../core/models/contracts';
import { RunHistory } from './run-history';

describe('Hosted report exports', () => {
  let fixture: ComponentFixture<RunHistory>;
  let http: HttpTestingController;
  let access: ApiAccess;
  let storedToken: string | null;
  let createUrl: jasmine.Spy;
  let revokeUrl: jasmine.Spy;
  let downloads: { href: string; name: string }[];
  const run = { id: 'run / 1', path: 'src/A.cs', level: 'file', kind: 'code', state: 'done',
    totalFiles: 1, completedFiles: 1, failedFiles: 0, skippedFiles: 0, usageOperations: 0,
    usage: { inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, reasoningOutputTokens: 0, durationMs: 0 },
    errors: [], costSpent: null, costCap: null, currency: null, tokenCap: null, createdAt: '' } as unknown as ReviewRun;

  beforeEach(async () => {
    storedToken = localStorage.getItem('qs-api-token');
    await TestBed.configureTestingModule({ imports: [RunHistory],
      providers: [provideHttpClient(withInterceptors([apiInterceptor])), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    access = TestBed.inject(ApiAccess);
    access.setToken('fixture-hosted-token');
    downloads = [];
    createUrl = spyOn(URL, 'createObjectURL').and.returnValue('blob:fixture-download');
    revokeUrl = spyOn(URL, 'revokeObjectURL');
    spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function (this: HTMLAnchorElement) {
      downloads.push({ href: this.href, name: this.download });
    });
    TestBed.inject(QualityApi).reviewRuns.set([run]);
    fixture = TestBed.createComponent(RunHistory);
    fixture.componentRef.setInput('node', { id: 'a', path: 'src/A.cs', name: 'A.cs', level: 'file', kinds: {}, children: [] });
    fixture.componentRef.setInput('activeKind', 'code');
    const component = fixture.componentInstance;
    component.runDrawerOpen.set(true);
    component.selectedRunId.set(run.id);
    component.runReport.set({ run: { id: run.id, revision: 1, completeness: 'complete' }, subject: { manifestHash: '' },
      execution: { reviewed: 1, reusedFresh: 0 }, summary: { score: 90, grade: 'A', findings: { total: 0 } },
      observations: [] } as unknown as QualityRunReport);
    // Prevent a baseline anchor from navigating Karma away while preserving component handlers.
    fixture.nativeElement.addEventListener('click', (event: Event) => event.preventDefault());
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    http.verify();
    if (storedToken === null) localStorage.removeItem('qs-api-token');
    else localStorage.setItem('qs-api-token', storedToken);
  });

  function click(selector: string): void {
    fixture.nativeElement.querySelector(selector).dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  }

  it('fetches run HTML with bearer auth and downloads a non-executable blob, then releases its URL', fakeAsync(() => {
    click('.run-exports button, .run-exports a');
    click('.run-exports button, .run-exports a'); // A second click shares the in-flight export.
    const request = http.expectOne('/api/repos/default/review/runs/run%20%2F%201/report?format=html');
    expect(request.request.headers.get('Authorization')).toBe('Bearer fixture-hosted-token');
    expect(request.request.responseType).toBe('blob');
    request.flush(new Blob(['<script>window.exportExecuted=true</script>'], { type: 'text/html' }));
    flushMicrotasks();
    expect(createUrl.calls.mostRecent().args[0].type).toBe('application/octet-stream');
    expect(downloads).toEqual([{ href: 'blob:fixture-download', name: 'quality-run-run / 1.html' }]);
    expect(document.querySelector('a[download]')).toBeNull();
    expect(revokeUrl).not.toHaveBeenCalled();
    tick(1000);
    expect(revokeUrl).toHaveBeenCalledOnceWith('blob:fixture-download');
  }));

  it('authenticates repository exports and shows failures without downloading error bodies', fakeAsync(() => {
    click('.commit-trend-note button, .commit-trend-note a');
    const request = http.expectOne('/api/repos/default/report?format=html');
    expect(request.request.headers.get('Authorization')).toBe('Bearer fixture-hosted-token');
    expect(request.request.responseType).toBe('blob');
    request.flush(new Blob(['denied']), { status: 403, statusText: 'Forbidden' });
    flushMicrotasks();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain('not permitted');
    expect(downloads).toEqual([]);
    expect(createUrl).not.toHaveBeenCalled();
    expect((fixture.nativeElement.querySelector('.commit-trend-note button') as HTMLButtonElement).disabled).toBeFalse();
  }));
});
