import { HttpBackend, HttpClient, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, from, switchMap } from 'rxjs';

interface LocalMutationTokenResponse {
  token: string;
  expiresAt: string;
}

@Injectable({ providedIn: 'root' })
export class LocalMutationTokenService {
  private readonly http = new HttpClient(inject(HttpBackend));
  private request: Promise<string> | null = null;

  token(): Promise<string> {
    this.request ??= firstValueFrom(
      this.http.get<LocalMutationTokenResponse>('/api/security/csrf'),
    ).then((response) => response.token);
    return this.request;
  }
}

export const localMutationProtectionInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/') || ['GET', 'HEAD', 'OPTIONS'].includes(request.method)) {
    return next(request);
  }

  const tokens = inject(LocalMutationTokenService);
  return from(tokens.token()).pipe(
    switchMap((token) =>
      next(
        request.clone({
          setHeaders: { 'X-Quality-CSRF-Token': token },
        }),
      ),
    ),
  );
};
