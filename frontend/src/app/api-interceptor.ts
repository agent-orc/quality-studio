import { HttpErrorResponse, HttpEvent, HttpHandlerFn, HttpRequest } from '@angular/common/http';
import { InjectionToken, inject } from '@angular/core';
import { Observable, retry, tap, throwError, timeout, timer } from 'rxjs';

import { ApiAccess } from './api-access';
import { ApiTimeoutError } from './api-errors';

export interface ApiRequestPolicy {
  /** Budget for an ordinary request, in milliseconds. */
  timeoutMs: number;
  /** Budget for requests that legitimately take longer, such as review preflight and matrix builds. */
  longRunningTimeoutMs: number;
  /** How often an idempotent GET is repeated after a transport failure. */
  retries: number;
  /** Base backoff between those repeats; the delay doubles per attempt. */
  retryDelayMs: number;
}

export const API_REQUEST_POLICY = new InjectionToken<ApiRequestPolicy>('API_REQUEST_POLICY', {
  providedIn: 'root',
  factory: (): ApiRequestPolicy => ({
    timeoutMs: 30_000,
    longRunningTimeoutMs: 180_000,
    retries: 2,
    retryDelayMs: 400,
  }),
});

/** Endpoints that start or inspect real work and may legitimately take minutes rather than seconds. */
const LONG_RUNNING = [
  /\/review$/,
  /\/review\/estimate$/,
  /\/security\/attack-coverage(\?|$)/,
  /\/guidelines\/impact$/,
  /\/repos\/import-from-agent-studio$/,
  /\/report(\?|$)/,
];

/** Transport-level failures worth repeating; a server verdict such as 404 or 409 never is. */
function retryable(request: HttpRequest<unknown>, error: unknown): boolean {
  if (request.method !== 'GET') return false;
  if (error instanceof ApiTimeoutError) return true;
  if (!(error instanceof HttpErrorResponse)) return false;
  return error.status === 0 || error.status === 502 || error.status === 503 || error.status === 504;
}

function budgetFor(url: string, policy: ApiRequestPolicy): number {
  return LONG_RUNNING.some(pattern => pattern.test(url)) ? policy.longRunningTimeoutMs : policy.timeoutMs;
}

/**
 * Every API request passes here, so no call can hang forever, a lost packet on a read does not
 * become a user-visible failure, and failures arrive as typed errors the shell can describe.
 */
export function apiInterceptor(request: HttpRequest<unknown>, next: HttpHandlerFn): Observable<HttpEvent<unknown>> {
  const policy = inject(API_REQUEST_POLICY);
  const access = inject(ApiAccess);
  const budget = budgetFor(request.urlWithParams, policy);
  const token = access.token();
  // A local API needs no credential, so without a configured token the request is unchanged.
  const authorized = token
    ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : request;
  return next(authorized).pipe(
    // Ahead of the retry, so every repeated attempt gets its own budget.
    timeout({ each: budget, with: () => throwError(() => new ApiTimeoutError(request.urlWithParams, budget)) }),
    retry({
      count: policy.retries,
      delay: (error, attempt) => retryable(request, error)
        ? timer(policy.retryDelayMs * 2 ** (attempt - 1))
        : throwError(() => error),
    }),
    tap({
      error: error => {
        if (error instanceof HttpErrorResponse && error.status === 401) access.reportUnauthorized();
      },
    }),
  );
}
