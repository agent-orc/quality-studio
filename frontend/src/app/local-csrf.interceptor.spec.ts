import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { fakeAsync, flushMicrotasks, TestBed } from '@angular/core/testing';
import { localCsrfInterceptor } from './local-csrf.interceptor';

describe('localCsrfInterceptor', () => {
  it('obtains a local session and secures mutations with its nonce', fakeAsync(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([localCsrfInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    const http = TestBed.inject(HttpTestingController);
    const httpClient = TestBed.inject(HttpClient);
    httpClient.post('/api/repos', { displayName: 'Example' }).subscribe();

    const session = http.expectOne('/api/security/session');
    expect(session.request.withCredentials).toBeTrue();
    session.flush({
      required: true,
      headerName: 'X-Quality-Studio-CSRF',
      token: 'nonce',
      expiresAt: '2099-01-01T00:00:00Z',
    });
    flushMicrotasks();

    const mutation = http.expectOne('/api/repos');
    expect(mutation.request.headers.get('X-Quality-Studio-CSRF')).toBe('nonce');
    expect(mutation.request.withCredentials).toBeTrue();
    mutation.flush({});
    http.verify();
  }));
});
