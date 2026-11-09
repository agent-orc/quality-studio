import { HttpClient, HttpHandlerFn, HttpRequest, HttpResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom, of } from 'rxjs';

import { ApiAccess, ApiSession, SESSION_URL } from './api-access';
import { API_REQUEST_POLICY, apiInterceptor, withMutationHeaders } from './api-interceptor';

const TOKEN_STORAGE_KEY = 'qs-api-token';

function clientSession(clientId: string): ApiSession {
  return {
    mode: 'hosted', kind: 'client', clientId, subject: null, role: null, repositories: ['default'], csrfToken: null, expiresAt: null,
    federation: { enabled: false, studioPublicUrl: null },
  };
}

describe('Mutation identity headers', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let access: ApiAccess;

  beforeEach(() => {
    localStorage.removeItem(TOKEN_STORAGE_KEY);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiInterceptor])),
        provideHttpClientTesting(),
        { provide: API_REQUEST_POLICY, useValue: { timeoutMs: 5_000, longRunningTimeoutMs: 5_000, retries: 0, retryDelayMs: 1 } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    access = TestBed.inject(ApiAccess);
  });

  afterEach(() => {
    backend.verify();
    localStorage.removeItem(TOKEN_STORAGE_KEY);
  });

  // Regression: hosted mode requires X-Client-Id on every mutation and the browser never sent it.
  it('sends the X-Client-Id the bearer token belongs to on a hosted mutation', async () => {
    access.setToken('hosted-token');
    const changing = firstValueFrom(http.post('/api/repos/default/findings/state', { state: 'accepted' }));

    const lookup = backend.expectOne(SESSION_URL);
    expect(lookup.request.method).toBe('GET');
    expect(lookup.request.headers.get('Authorization')).toBe('Bearer hosted-token');
    lookup.flush(clientSession('alice'));

    const mutation = backend.expectOne('/api/repos/default/findings/state');
    expect(mutation.request.headers.get('Authorization')).toBe('Bearer hosted-token');
    expect(mutation.request.headers.get('X-Client-Id')).toBe('alice');
    mutation.flush({ state: 'accepted' });
    await changing;

    // Read once: the next mutation reuses it.
    const second = firstValueFrom(http.delete('/api/repos/default/findings/suppressions/x'));
    const repeated = backend.expectOne('/api/repos/default/findings/suppressions/x');
    expect(repeated.request.headers.get('X-Client-Id')).toBe('alice');
    repeated.flush({});
    await second;
  });

  it('reads the client id again after the token changes', async () => {
    access.setToken('first-token');
    access.session.set(clientSession('first'));
    access.setToken('second-token');
    const changing = firstValueFrom(http.put('/api/repos/default', {}));

    backend.expectOne(SESSION_URL).flush(clientSession('second'));
    const mutation = backend.expectOne('/api/repos/default');
    expect(mutation.request.headers.get('X-Client-Id')).toBe('second');
    mutation.flush({});
    await changing;
  });

  it('still sends the mutation when an older API has no session route', async () => {
    access.setToken('hosted-token');
    const changing = firstValueFrom(http.post('/api/review', {}));

    backend.expectOne(SESSION_URL).flush({}, { status: 404, statusText: 'Not Found' });
    const mutation = backend.expectOne('/api/review');
    expect(mutation.request.headers.has('X-Client-Id')).toBeFalse();
    mutation.flush({});
    await changing;
  });

  it('leaves reads and local-mode mutations untouched', async () => {
    const reading = firstValueFrom(http.get('/api/repos'));
    const read = backend.expectOne('/api/repos');
    expect(read.request.headers.has('X-Client-Id')).toBeFalse();
    read.flush({});
    await reading;

    const changing = firstValueFrom(http.post('/api/review', {}));
    const mutation = backend.expectOne('/api/review');
    expect(mutation.request.headers.has('X-Client-Id')).toBeFalse();
    expect(mutation.request.headers.has('X-CSRF-Token')).toBeFalse();
    mutation.flush({});
    await changing;
  });

  it('asks the shell to sign in again when a federated session ended', async () => {
    const loading = firstValueFrom(http.get('/api/repos'));
    backend.expectOne('/api/repos').flush({}, {
      status: 401, statusText: 'Unauthorized', headers: { 'X-QS-Sign-In': 'agent-studio' },
    });

    await expectAsync(loading).toBeRejected();
    expect(access.sessionEndedAt()).toBe(1);
    expect(access.unauthorizedAt()).withContext('no token dialog for a Studio user').toBe(0);
  });
});

describe('Hand-off session CSRF header', () => {
  const access = { token: () => '', session: () => null } as unknown as ApiAccess;
  const next: HttpHandlerFn = () => of(new HttpResponse({ status: 200 }));

  it('repeats the session CSRF cookie on an unsafe request', async () => {
    const prepared = await firstValueFrom(withMutationHeaders(new HttpRequest('POST', '/api/review', {}), access, next,
      'theme=dark; __Host-qs-csrf=0123abcd; other=1'));

    expect(prepared.headers.get('X-CSRF-Token')).toBe('0123abcd');
    expect(prepared.headers.has('X-Client-Id')).toBeFalse();
  });

  it('adds nothing to a read or without the cookie', async () => {
    const read = await firstValueFrom(withMutationHeaders(new HttpRequest('GET', '/api/repos'), access, next, '__Host-qs-csrf=0123abcd'));
    const local = await firstValueFrom(withMutationHeaders(new HttpRequest('POST', '/api/review', {}), access, next, 'theme=dark'));

    expect(read.headers.has('X-CSRF-Token')).toBeFalse();
    expect(local.headers.has('X-CSRF-Token')).toBeFalse();
  });
});
