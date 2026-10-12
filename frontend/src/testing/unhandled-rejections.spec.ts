import { HttpErrorResponse } from '@angular/common/http';

// Zone.js reports a promise rejection nobody handled only as a console ERROR line: its
// PromiseRejectionEvent dispatch throws for a raw rejection value, so Jasmine never sees it and
// the leaking spec passes. The top-level hooks below turn every such rejection into a failure
// of the spec (or, after the last spec, of the run) instead of a stray log line.
interface ZoneGlobal {
  __symbol__(name: string): string;
  [hook: string]: unknown;
}

type RejectionHandler = (this: unknown, error: unknown) => void;

const zone = (globalThis as unknown as { Zone: ZoneGlobal }).Zone;
const handlerKey = zone.__symbol__('unhandledPromiseRejectionHandler');
const zoneHandler = zone[handlerKey] as RejectionHandler | undefined;
const leaked: unknown[] = [];

zone[zone.__symbol__('ignoreConsoleErrorUncaughtError')] = true;
zone[handlerKey] = function (this: unknown, error: unknown) {
  leaked.push(error);
  zoneHandler?.call(this, error);
} satisfies RejectionHandler;

function describeRejection(error: unknown): string {
  const rejection = error instanceof Error && 'rejection' in error ? error.rejection : error;
  if (rejection instanceof HttpErrorResponse) {
    return `HttpErrorResponse ${rejection.status} for ${rejection.url ?? 'unknown url'}`;
  }
  return rejection instanceof Error ? rejection.message : String(rejection);
}

function expectNoUnhandledRejections(): void {
  expect(leaked.splice(0).map(describeRejection))
    .withContext('unhandled promise rejections; attach the rejection handler before the promise settles')
    .toEqual([]);
}

afterEach(expectNoUnhandledRejections);
afterAll(expectNoUnhandledRejections);

describe('unhandled promise rejection guard', () => {
  it('records a rejection nobody handled so the leaking spec fails', async () => {
    void Promise.reject(new HttpErrorResponse({ status: 503, statusText: 'Unavailable', url: '/api/repos/default/tree' }));
    await new Promise(resolve => setTimeout(resolve));

    expect(leaked.splice(0).map(describeRejection)).toEqual(['HttpErrorResponse 503 for /api/repos/default/tree']);
  });

  it('ignores a rejection that is handled before the promise settles', async () => {
    const failing = Promise.reject(new HttpErrorResponse({ status: 503, url: '/api/repos/default/tree' }));
    await expectAsync(failing).toBeRejectedWith(jasmine.objectContaining({ status: 503 }));
    await new Promise(resolve => setTimeout(resolve));

    expect(leaked).toEqual([]);
  });
});
