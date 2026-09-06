import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ApiAccess } from './api-access';
import { App } from './app';
import { ReviewFinding } from './contracts';
import { QualityApi } from './quality-api';

const URL_SETTLE_MS = 200;

function settle(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, URL_SETTLE_MS));
}

function finding(fingerprint: string): ReviewFinding {
  return {
    id: fingerprint, fingerprint, ruleId: 'rule', aspect: 'correctness', severity: 'high',
    title: 'Finding', description: 'Description.', recommendation: 'Fix.',
    locations: [{ path: 'src/A.cs', range: { start: { line: 1, column: 1 }, end: { line: 1, column: 4 } } }],
  };
}

describe('App shell URL state', () => {
  let fixture: ComponentFixture<App>;
  let app: App;
  let http: HttpTestingController;
  let pushed: string[];
  let replaced: string[];
  let pushSpy: jasmine.Spy;
  let replaceSpy: jasmine.Spy;
  const originalUrl = location.href;

  beforeEach(async () => {
    history.replaceState(null, '', `${location.pathname}?path=src/A.cs&kind=code`);
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    pushed = [];
    replaced = [];
    pushSpy = spyOn(history, 'pushState').and.callFake((_data, _title, url) => { pushed.push(String(url)); });
    replaceSpy = spyOn(history, 'replaceState').and.callFake((_data, _title, url) => { replaced.push(String(url)); });

    fixture = TestBed.createComponent(App);
    app = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await settle();
    pushed.length = 0;
    replaced.length = 0;
  });

  afterEach(() => {
    fixture.destroy();
    http.match(() => true).forEach(request => request.flush({}, { status: 503, statusText: 'Unavailable' }));
    replaceSpy.and.callThrough();
    history.replaceState(null, '', originalUrl);
  });

  it('collapses a salvo of finding selections into one history write', async () => {
    app.selectFinding(finding('sha256:a'));
    app.selectFinding(finding('sha256:b'));
    app.selectFinding(finding('sha256:c'));
    fixture.detectChanges();
    await settle();

    expect(replaced.length).toBe(1);
    expect(replaced[0]).toContain('finding=sha256%3Ac');
    expect(pushed.length).toBe(0);
  });

  it('pushes an entry for a new position and replaces it while refining the same one', async () => {
    app.selected.set('src/B.cs');
    fixture.detectChanges();
    await settle();
    expect(pushed.length).toBe(1);
    expect(pushed[0]).toContain('path=src%2FB.cs');

    app.selectKind('security');
    fixture.detectChanges();
    await settle();
    expect(pushed.length).toBe(1);
    expect(replaced.length).toBe(1);
    expect(replaced[0]).toContain('kind=security');
  });

  it('restores the position from the URL on popstate instead of leaving the shell', () => {
    replaceSpy.and.callThrough();
    history.replaceState(null, '', `${location.pathname}?path=src/C.cs&kind=performance&finding=sha256%3Az&location=2`);

    app.onPopState();

    expect(app.selected()).toBe('src/C.cs');
    expect(app.activeKind()).toBe('performance');
    expect(app.selectedFindingFingerprint()).toBe('sha256:z');
    expect(app.selectedLocationIndex()).toBe(2);
  });

  it('does not push a second entry for the position a popstate just restored', async () => {
    replaceSpy.and.callThrough();
    history.replaceState(null, '', `${location.pathname}?path=src/C.cs&kind=code`);
    app.onPopState();
    replaceSpy.and.callFake((_data: unknown, _title: string, url?: string | URL | null) => { replaced.push(String(url)); });
    fixture.detectChanges();
    await settle();

    expect(pushed.length).toBe(0);
    expect(pushSpy).not.toHaveBeenCalled();
  });

  it('shares one tree flattening between the shell and its panes', () => {
    const api = TestBed.inject(QualityApi);
    const kinds = {} as never;
    api.tree.set([{ id: 'root', name: 'Root', level: 'repository', path: '.', kinds, children: [
      { id: 'a', name: 'A.cs', level: 'file', path: 'src/A.cs', kinds, children: [] },
    ] }]);

    expect(api.allNodes()).toBe(api.allNodes());
    expect(api.nodeAt('src/A.cs')?.name).toBe('A.cs');
    expect(api.nodeAt('src/missing.cs')).toBeUndefined();
  });

  it('opens the API access dialog when the API rejects a request as unauthenticated', () => {
    expect(app.apiAccessDialogOpen()).toBeFalse();

    TestBed.inject(ApiAccess).reportUnauthorized();
    fixture.detectChanges();

    expect(app.apiAccessDialogOpen()).toBeTrue();
    expect(app.apiAccessRejected()).withContext('the dialog explains why it opened').toBeTrue();

    app.closeApiAccess();
    expect(app.apiAccessRejected()).toBeFalse();
  });
});
