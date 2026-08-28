import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CSRF_NONCE_ENDPOINT, CSRF_NONCE_HEADER, csrfNonceInterceptor } from './csrf-nonce.interceptor';

describe('csrfNonceInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([csrfNonceInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('fetches a nonce before a mutation and attaches it as a header', async () => {
    const result = http.post('/api/repos', { id: 'default' });
    const promise = new Promise<void>((resolve, reject) => {
      result.subscribe({ next: () => resolve(), error: reject });
    });

    const nonceRequest = backend.expectOne(CSRF_NONCE_ENDPOINT);
    expect(nonceRequest.request.method).toBe('GET');
    nonceRequest.flush({ nonce: 'test-nonce-value' });

    const mutation = backend.expectOne('/api/repos');
    expect(mutation.request.headers.get(CSRF_NONCE_HEADER)).toBe('test-nonce-value');
    mutation.flush({});

    await promise;
  });

  it('falls back to sending the mutation without a nonce when the endpoint is unavailable', async () => {
    const result = http.delete('/api/repos/default');
    const promise = new Promise<void>((resolve, reject) => {
      result.subscribe({ next: () => resolve(), error: reject });
    });

    backend.expectOne(CSRF_NONCE_ENDPOINT).flush('not found', { status: 404, statusText: 'Not Found' });

    const mutation = backend.expectOne('/api/repos/default');
    expect(mutation.request.headers.has(CSRF_NONCE_HEADER)).toBeFalse();
    mutation.flush({});

    await promise;
  });

  it('does not fetch a nonce for read-only requests', async () => {
    const result = http.get('/api/repos');
    const promise = new Promise<void>((resolve, reject) => {
      result.subscribe({ next: () => resolve(), error: reject });
    });

    backend.expectOne('/api/repos').flush({});
    await promise;
  });

  it('does not re-fetch a nonce when the request already carries one', async () => {
    const result = http.post('/api/repos', { id: 'default' }, { headers: { [CSRF_NONCE_HEADER]: 'already-set' } });
    const promise = new Promise<void>((resolve, reject) => {
      result.subscribe({ next: () => resolve(), error: reject });
    });

    const mutation = backend.expectOne('/api/repos');
    expect(mutation.request.headers.get(CSRF_NONCE_HEADER)).toBe('already-set');
    mutation.flush({});

    await promise;
  });
});
