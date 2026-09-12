import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ApiTimeoutError, describeHttpError } from './api-errors';
import { ApiContext } from './api-context';
import { API_REQUEST_POLICY, apiInterceptor } from './api-interceptor';

describe('API request policy', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiInterceptor])),
        provideHttpClientTesting(),
        {
          provide: API_REQUEST_POLICY,
          useValue: { timeoutMs: 60, longRunningTimeoutMs: 400, retries: 2, retryDelayMs: 1 },
        },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  it('cancels a request that outruns its budget and reports it as a timeout', async () => {
    const request = firstValueFrom(http.get('/api/repos/default/tree'));
    const first = backend.expectOne('/api/repos/default/tree');

    await expectAsync(request).toBeRejectedWith(jasmine.any(ApiTimeoutError));
    expect(TestBed.inject(ApiContext).connectionState()).toBe('offline');
    expect(first.cancelled).withContext('the hanging request is aborted, not left open').toBeTrue();
    expect(backend.match(() => true).every(pending => pending.cancelled)).toBeTrue();
  });

  it('grants review preflight a longer budget than an ordinary read', fakeAsync(() => {
    let response: unknown;
    http.post('/api/repos/default/review/estimate', {}).subscribe(value => response = value);
    const pending = backend.expectOne('/api/repos/default/review/estimate');

    tick(120);
    expect(pending.cancelled).withContext('still within the long-running budget').toBeFalse();

    pending.flush({ estimate: {} });
    expect(response).toEqual({ estimate: {} });
    backend.verify();
  }));

  it('repeats an idempotent read after a transport failure and succeeds on the retry', async () => {
    const loading = firstValueFrom(http.get<{ nodes: string[] }>('/api/repos/default/tree'));
    backend.expectOne('/api/repos/default/tree').error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

    await new Promise(resolve => setTimeout(resolve, 10));
    backend.expectOne('/api/repos/default/tree').flush({ nodes: ['ok'] });

    expect((await loading).nodes).toEqual(['ok']);
  });

  it('gives up after the configured number of retries', async () => {
    const loading = firstValueFrom(http.get('/api/repos/default/tree'));
    for (let attempt = 0; attempt < 3; attempt++) {
      backend.expectOne('/api/repos/default/tree').error(new ProgressEvent('error'), { status: 503, statusText: 'Unavailable' });
      await new Promise(resolve => setTimeout(resolve, 10));
    }

    await expectAsync(loading).toBeRejected();
    backend.verify();
  });

  for (const status of [0, 502, 503, 504]) {
    it(`reports a background API failure with status ${status} immediately, before retries finish`, async () => {
      const context = TestBed.inject(ApiContext);
      context.connectionState.set('live');
      const loading = firstValueFrom(http.get('/api/quotas'));
      backend.expectOne('/api/quotas').error(new ProgressEvent('error'), { status, statusText: 'Unavailable' });
      expect(context.connectionState()).toBe('offline');
      expect(context.connectionError()).not.toBe('');
      await new Promise(resolve => setTimeout(resolve, 10));
      backend.expectOne('/api/quotas').flush({ providers: [] });
      await loading;
      expect(context.connectionState()).withContext('a single endpoint cannot substitute for registry recovery').toBe('offline');
    });
  }

  it('recognizes an empty plain-text proxy 500 immediately and repeats only the read', async () => {
    const context = TestBed.inject(ApiContext);
    context.connectionState.set('live');
    const loading = firstValueFrom(http.get('/api/repos'));
    backend.expectOne('/api/repos').flush('', {
      status: 500, statusText: 'Internal Server Error', headers: { 'Content-Type': 'text/plain; charset=utf-8' },
    });

    expect(context.connectionState()).toBe('offline');
    expect(context.connectionError()).toBe('The API is not reachable. Check that the Quality Studio API is running.');
    await new Promise(resolve => setTimeout(resolve, 10));
    backend.expectOne('/api/repos').flush({ repositories: [] });
    await loading;

    context.connectionState.set('live');
    const posting = firstValueFrom(http.post('/api/repos/default/threads', {}));
    backend.expectOne('/api/repos/default/threads').flush(null, {
      status: 500, statusText: 'Internal Server Error', headers: { 'Content-Type': 'text/plain' },
    });
    await expectAsync(posting).toBeRejected();
    expect(context.connectionState()).toBe('offline');
    expect(context.connectionError()).toContain('The API is not reachable.');
    backend.verify();
  });

  it('keeps API problem responses and other 500 verdicts local to their feature', async () => {
    const context = TestBed.inject(ApiContext);
    context.connectionState.set('live');
    const verdicts = [
      { body: { type: 'https://quality-studio/errors/review-failed', title: 'Review failed', detail: 'The runner failed.' },
        contentType: 'application/problem+json', message: 'The runner failed.' },
      { body: null, contentType: 'application/json', message: 'The API reported an internal error (HTTP 500).' },
      { body: 'The runner failed.', contentType: 'text/plain', message: 'The runner failed.' },
    ];
    for (const verdict of verdicts) {
      const posting = firstValueFrom(http.post('/api/repos/default/review', {})).catch((error: unknown) => error);
      backend.expectOne('/api/repos/default/review').flush(verdict.body, {
        status: 500, statusText: 'Internal Server Error', headers: { 'Content-Type': verdict.contentType },
      });

      expect(describeHttpError(await posting)).toBe(verdict.message);
      expect(context.connectionState()).toBe('live');
      expect(context.connectionError()).toBe('');
    }
    backend.verify();
  });

  it('keeps request-specific server errors separate from global API availability', async () => {
    const context = TestBed.inject(ApiContext);
    context.connectionState.set('live');
    const missing = firstValueFrom(http.get('/api/repos/default/file'));
    backend.expectOne('/api/repos/default/file').flush({ detail: 'Missing.' }, { status: 404, statusText: 'Not Found' });
    await expectAsync(missing).toBeRejected();
    expect(context.connectionState()).toBe('live');
    backend.verify();
  });

  it('keeps a structured API dependency failure local to the requesting feature', async () => {
    const context = TestBed.inject(ApiContext);
    context.connectionState.set('live');
    const loading = firstValueFrom(http.post('/api/repos/default/security/scan', {}));
    backend.expectOne('/api/repos/default/security/scan').flush({
      type: 'https://quality-studio/errors/scanner-unavailable', title: 'Scanner unavailable',
      detail: 'Agent Studio target unavailable.',
    }, { status: 503, statusText: 'Unavailable' });

    await expectAsync(loading).toBeRejected();
    expect(context.connectionState()).toBe('live');
    expect(context.connectionError()).toBe('');
    backend.verify();
  });

  it('never repeats a mutation or a server verdict', async () => {
    const posting = firstValueFrom(http.post('/api/repos/default/threads', {}));
    backend.expectOne('/api/repos/default/threads').error(new ProgressEvent('error'), { status: 503, statusText: 'Unavailable' });
    await expectAsync(posting).toBeRejected();

    const reading = firstValueFrom(http.get('/api/repos/default/file'));
    backend.expectOne('/api/repos/default/file').flush('nope', { status: 404, statusText: 'Not Found' });
    await expectAsync(reading).toBeRejected();

    backend.verify();
  });
});

describe('API error text', () => {
  it('prefers the problem detail the API sent', () => {
    const error = new HttpErrorResponse({ status: 409, statusText: 'Conflict', error: { detail: 'The finding changed.' } });
    expect(describeHttpError(error)).toBe('The finding changed.');
  });

  it('states the transport problem instead of the framework wording', () => {
    const unreachable = new HttpErrorResponse({ status: 0, statusText: 'Unknown Error', url: '/api/repos/default/tree' });
    expect(describeHttpError(unreachable)).toBe('The API is not reachable. Check that the Quality Studio API is running.');

    const failed = new HttpErrorResponse({ status: 500, statusText: 'Server Error' });
    expect(describeHttpError(failed)).toBe('The API reported an internal error (HTTP 500).');
    expect(describeHttpError(failed)).not.toContain('Http failure response');

    expect(describeHttpError(new ApiTimeoutError('/api/repos/default/tree', 30_000)))
      .toContain('did not answer within 30 s');
  });

  it('names the missing token for an unauthenticated request', () => {
    const denied = new HttpErrorResponse({ status: 401, statusText: 'Unauthorized' });
    expect(describeHttpError(denied)).toContain('API access token');
  });
});
