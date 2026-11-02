import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse, HttpEvent, HttpHandlerFn, HttpHeaders, HttpRequest, HttpResponse } from '@angular/common/http';
import { InjectionToken, inject } from '@angular/core';
import { Observable, catchError, defaultIfEmpty, filter, first, map, of, retry, switchMap, tap, throwError, timeout, timer } from 'rxjs';

import { ApiAccess, ApiSession, SESSION_URL } from './api-access';
import { ApiContext } from './api-context';
import { ApiTimeoutError, isUnreachable } from './api-errors';

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
  /\/change-review$/,
  /\/security\/attack-coverage(\?|$)/,
  /\/guidelines\/impact$/,
  /\/repos\/import-from-agent-studio$/,
  /\/report(\?|$)/,
];

/** Transport-level failures worth repeating; a server verdict such as 404 or 409 never is. */
function retryable(request: HttpRequest<unknown>, error: unknown): boolean {
  if (request.method !== 'GET') return false;
  if (isUnreachable(error)) return true;
  if (!(error instanceof HttpErrorResponse)) return false;
  return error.status === 502 || error.status === 503 || error.status === 504;
}

function budgetFor(url: string, policy: ApiRequestPolicy): number {
  return LONG_RUNNING.some(pattern => pattern.test(url)) ? policy.longRunningTimeoutMs : policy.timeoutMs;
}

/** Credentials and API-specific recovery belong only to this origin's API namespace. */
function isApiRequest(url: string, origin: string | undefined): boolean {
  if (!origin || url.startsWith('//') || url.includes('\\')) return false;
  if (!url.startsWith('/') && !/^https?:\/\//i.test(url)) return false;
  try {
    const target = new URL(url, origin);
    return target.origin === origin
      && !target.username && !target.password
      && (target.pathname === '/api' || target.pathname.startsWith('/api/'));
  } catch {
    return false;
  }
}

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
/** Marks a 401 from a host federated with Agent Studio: a page reload signs in through Studio again. */
const SIGN_IN_HEADER = 'X-QS-Sign-In';
/** Set next to the session cookie by the Agent Studio hand-off; readable by this origin's pages only. */
const CSRF_COOKIE = '__Host-qs-csrf';

/**
 * A hosted API identifies the caller of a mutation twice. A hand-off session repeats its CSRF
 * cookie as X-CSRF-Token. A bearer client sends the X-Client-Id its token belongs to, which
 * GET /api/v1/session reports; it is read once before the first mutation and kept until the
 * token changes. Local mode has neither and sends the request unchanged.
 */
export function withMutationHeaders(request: HttpRequest<unknown>, access: ApiAccess, next: HttpHandlerFn,
  cookies: string): Observable<HttpRequest<unknown>> {
  if (!UNSAFE_METHODS.has(request.method)) return of(request);
  const token = access.token();
  if (!token) {
    const csrf = readCookie(cookies, CSRF_COOKIE);
    return of(csrf ? request.clone({ setHeaders: { 'X-CSRF-Token': csrf } }) : request);
  }
  const known = access.session();
  const session = known ? of(known) : next(new HttpRequest('GET', SESSION_URL, {
    headers: new HttpHeaders({ Authorization: `Bearer ${token}` }),
  })).pipe(
    filter((event): event is HttpResponse<ApiSession> => event instanceof HttpResponse),
    first(),
    map(response => {
      if (!response.body?.clientId) throw new Error('The API session did not identify its bearer client.');
      return response.body;
    }),
    tap(body => { if (body && access.token() === token) access.session.set(body); }),
    // An older API has no session route; the mutation then goes out as it always did.
    catchError(error => error instanceof HttpErrorResponse && error.status === 404
      ? of(null)
      : throwError(() => error)),
    defaultIfEmpty(null),
  );
  return session.pipe(map(identity => {
    if (identity === null) return request;
    if (!identity.clientId) throw new Error('The API session did not identify its bearer client.');
    return request.clone({ setHeaders: { 'X-Client-Id': identity.clientId } });
  }));
}

function readCookies(document: Document): string {
  try {
    return document.cookie ?? '';
  } catch {
    // A sandboxed document may refuse cookie access; such a page has no session cookie either.
    return '';
  }
}

function readCookie(cookies: string, name: string): string | null {
  for (const part of cookies.split(';')) {
    const separator = part.indexOf('=');
    if (separator > 0 && part.slice(0, separator).trim() === name) return part.slice(separator + 1).trim() || null;
  }
  return null;
}

/**
 * Every API request passes here, so no call can hang forever, a lost packet on a read does not
 * become a user-visible failure, and failures arrive as typed errors the shell can describe.
 */
export function apiInterceptor(request: HttpRequest<unknown>, next: HttpHandlerFn): Observable<HttpEvent<unknown>> {
  const document = inject(DOCUMENT);
  if (!isApiRequest(request.url, document.location?.origin)) return next(request);
  const policy = inject(API_REQUEST_POLICY);
  const access = inject(ApiAccess);
  const context = inject(ApiContext);
  const budget = budgetFor(request.urlWithParams, policy);
  const token = access.token();
  // A local API needs no credential, so without a configured token the request is unchanged.
  const authorized = token
    ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : request;
  return withMutationHeaders(authorized, access, next, readCookies(document)).pipe(
    switchMap(prepared => next(prepared)),
    // Ahead of the retry, so every repeated attempt gets its own budget.
    timeout({ each: budget, with: () => throwError(() => new ApiTimeoutError(request.urlWithParams, budget)) }),
    // Report the first failed attempt, including failures from periodic background reads.
    // Recovery revalidates the registry and selected repository before declaring the app live.
    tap({ error: error => context.reportFailure(error) }),
    retry({
      count: policy.retries,
      delay: (error, attempt) => retryable(request, error)
        ? timer(policy.retryDelayMs * 2 ** (attempt - 1))
        : throwError(() => error),
    }),
    tap({
      error: error => {
        if (!(error instanceof HttpErrorResponse) || error.status !== 401) return;
        if (!token && error.headers.get(SIGN_IN_HEADER) === 'agent-studio') access.reportSessionEnded();
        else access.reportUnauthorized();
      },
    }),
  );
}
