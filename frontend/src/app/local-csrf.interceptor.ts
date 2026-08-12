import { inject, Injectable } from '@angular/core';
import { HttpBackend, HttpClient, HttpInterceptorFn } from '@angular/common/http';
import { firstValueFrom, from, map, switchMap } from 'rxjs';

interface LocalSecuritySession {
  required: boolean;
  headerName: string | null;
  token: string | null;
  expiresAt: string | null;
}

@Injectable({ providedIn: 'root' })
class LocalCsrfSession {
  private readonly backend = inject(HttpBackend);
  private session?: Promise<LocalSecuritySession>;
  private expiresAt = 0;

  get(): Promise<LocalSecuritySession> {
    if (Date.now() >= this.expiresAt - 30_000) this.session = undefined;
    this.session ??= firstValueFrom(
      new HttpClient(this.backend).get<LocalSecuritySession>('/api/security/session', {
        withCredentials: true,
      }),
    )
      .then((session) => {
        this.expiresAt = session.expiresAt
          ? Date.parse(session.expiresAt)
          : Number.POSITIVE_INFINITY;
        return session;
      })
      .catch((error: unknown) => {
        this.session = undefined;
        throw error;
      });
    return this.session;
  }
}

const safeMethods = new Set(['GET', 'HEAD', 'OPTIONS']);
const protectedSensorGet = /\/(?:scan|security\/(?:scan|attack-coverage))(?:[?#]|$)/;

export const localCsrfInterceptor: HttpInterceptorFn = (request, next) => {
  if (
    (safeMethods.has(request.method) &&
      !(request.method === 'GET' && protectedSensorGet.test(request.url))) ||
    !request.url.startsWith('/api') ||
    request.url === '/api/security/session'
  ) {
    return next(request);
  }

  return from(inject(LocalCsrfSession).get()).pipe(
    map((session) => {
      if (!session.required || !session.headerName || !session.token) {
        return request;
      }
      return request.clone({
        setHeaders: { [session.headerName]: session.token },
        withCredentials: true,
      });
    }),
    switchMap((securedRequest) => next(securedRequest)),
  );
};
