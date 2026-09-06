import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ApiAccess } from './api-access';
import { API_REQUEST_POLICY, apiInterceptor } from './api-interceptor';

const TOKEN_STORAGE_KEY = 'qs-api-token';

describe('API access token', () => {
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

  afterEach(() => localStorage.removeItem(TOKEN_STORAGE_KEY));

  it('sends no Authorization header in local mode', async () => {
    const loading = firstValueFrom(http.get('/api/repos'));
    const request = backend.expectOne('/api/repos');

    expect(access.configured()).toBeFalse();
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({ repositories: [] });
    await loading;
  });

  it('attaches the configured token as a bearer credential', async () => {
    access.setToken('  hosted-token  ');
    const loading = firstValueFrom(http.get('/api/repos'));
    const request = backend.expectOne('/api/repos');

    expect(request.request.headers.get('Authorization')).toBe('Bearer hosted-token');
    request.flush({ repositories: [] });
    await loading;
  });

  it('persists the token under qs-api-token and restores it', () => {
    access.setToken('persisted');
    expect(localStorage.getItem(TOKEN_STORAGE_KEY)).toBe('persisted');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    expect(TestBed.inject(ApiAccess).token()).toBe('persisted');
  });

  it('removes the stored token when access is cleared', () => {
    access.setToken('persisted');
    access.clear();

    expect(access.configured()).toBeFalse();
    expect(localStorage.getItem(TOKEN_STORAGE_KEY)).toBeNull();
  });

  it('survives unavailable storage', () => {
    const getItem = spyOn(Storage.prototype, 'getItem').and.throwError('denied');
    const setItem = spyOn(Storage.prototype, 'setItem').and.throwError('denied');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const isolated = TestBed.inject(ApiAccess);

    expect(isolated.token()).toBe('');
    expect(() => isolated.setToken('session-only')).not.toThrow();
    expect(isolated.token()).toBe('session-only');
    expect(getItem).toHaveBeenCalled();
    expect(setItem).toHaveBeenCalled();
  });

  it('reports a rejected request so the shell can ask for a token', async () => {
    const before = access.unauthorizedAt();
    const loading = firstValueFrom(http.get('/api/repos'));
    backend.expectOne('/api/repos').flush('denied', { status: 401, statusText: 'Unauthorized' });

    await expectAsync(loading).toBeRejected();
    expect(access.unauthorizedAt()).toBe(before + 1);
  });

  it('does not report a rejection for an ordinary failure', async () => {
    const before = access.unauthorizedAt();
    const loading = firstValueFrom(http.get('/api/repos'));
    backend.expectOne('/api/repos').flush('gone', { status: 404, statusText: 'Not Found' });

    await expectAsync(loading).toBeRejected();
    expect(access.unauthorizedAt()).toBe(before);
  });
});
