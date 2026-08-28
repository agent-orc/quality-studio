import { HttpBackend, HttpClient, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, of, switchMap } from 'rxjs';

const MUTATION_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
export const CSRF_NONCE_HEADER = 'X-Csrf-Nonce';
export const CSRF_NONCE_ENDPOINT = '/api/security/csrf-nonce';

/**
 * Attaches a per-session anti-CSRF nonce to mutation requests. The nonce is fetched through
 * HttpBackend (bypassing this interceptor) to avoid recursing into itself. Hosted mode has no
 * nonce endpoint (it relies on bearer credentials instead), so a failed fetch there falls back to
 * sending the mutation unmodified rather than blocking every write.
 */
export const csrfNonceInterceptor: HttpInterceptorFn = (req, next) => {
  if (!MUTATION_METHODS.has(req.method) || req.headers.has(CSRF_NONCE_HEADER)) {
    return next(req);
  }

  const rawHttp = new HttpClient(inject(HttpBackend));
  return rawHttp.get<{ nonce: string }>(CSRF_NONCE_ENDPOINT).pipe(
    catchError(() => of(null)),
    switchMap(result =>
      next(result ? req.clone({ setHeaders: { [CSRF_NONCE_HEADER]: result.nonce } }) : req)),
  );
};
