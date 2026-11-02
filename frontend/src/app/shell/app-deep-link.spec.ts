import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { apiInterceptor } from '../core/api/api-interceptor';
import { App } from './app';

function settle(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, 200));
}

describe('App shell deep link v1', () => {
  const originalUrl = location.href;
  let fixture: ComponentFixture<App> | null = null;
  let http: HttpTestingController;
  let replaced: string[];

  async function open(search: string): Promise<App> {
    history.replaceState(null, '', `${location.pathname}${search}`);
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(withInterceptors([apiInterceptor])), provideHttpClientTesting()],
    }).compileComponents();
    replaced = [];
    spyOn(history, 'replaceState').and.callFake((_data, _title, url) => { replaced.push(String(url)); });
    spyOn(history, 'pushState');
    fixture = TestBed.createComponent(App);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    http.match(() => true).forEach(request => request.flush({}, { status: 503, statusText: 'Unavailable' }));
    (history.replaceState as jasmine.Spy).and.callThrough();
    history.replaceState(null, '', originalUrl);
  });

  it('opens the repository dashboard for view=dashboard and drops the view once the position is in the URL', async () => {
    const app = await open('?repo=default&view=dashboard&path=src/A.cs&finding=sha256%3Aa');

    expect(app.selected()).toBe('.');
    expect(app.selectedFindingFingerprint()).toBeNull();
    await settle();
    expect(replaced.at(-1)).not.toContain('view=');
    expect(replaced.at(-1)).toContain('path=.');
  });

  it('treats a view it does not know as the dashboard and keeps the explorer position otherwise', async () => {
    const app = await open('?repo=default&view=change&path=src/A.cs');
    expect(app.selected()).toBe('.');
  });

  it('keeps the explorer position', async () => {
    const app = await open('?repo=default&view=explorer&path=src/A.cs');
    expect(app.selected()).toBe('src/A.cs');
  });

  it('renders "Back to <card>" to the Studio the API names', async () => {
    await open('?repo=default&card=AGT-3352');

    http.expectOne('/api/v1/session').flush({
      mode: 'hosted', kind: 'session', clientId: null, subject: 'robert', role: 'operator', repositories: ['default'],
      csrfToken: 'x', federation: { enabled: true, studioPublicUrl: 'https://studio.example' },
    });
    await settle();
    fixture!.detectChanges();

    const link = fixture!.nativeElement.querySelector('.back-to-card') as HTMLAnchorElement;
    expect(link).not.toBeNull();
    expect(link.textContent?.trim()).toBe('← Back to AGT-3352');
    expect(link.getAttribute('href')).toBe('https://studio.example');
  });

  it('shows no back link without a Studio, as in local mode', async () => {
    await open('?repo=default&card=AGT-3352');

    http.expectOne('/api/v1/session').flush({
      mode: 'local', kind: 'local', clientId: null, subject: null, role: null, repositories: ['*'], csrfToken: null,
      federation: { enabled: false, studioPublicUrl: null },
    });
    await settle();
    fixture!.detectChanges();

    expect(fixture!.nativeElement.querySelector('.back-to-card')).toBeNull();
  });
});
