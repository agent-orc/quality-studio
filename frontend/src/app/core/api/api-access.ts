import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

const TOKEN_STORAGE_KEY = 'qs-api-token';
export const SESSION_URL = '/api/v1/session';

/** GET /api/v1/session: who this browser is to the API, and what its mutations must carry. */
export interface ApiSession {
  mode: 'local' | 'hosted';
  /** local: Local mode; client: a bearer token; session: a session opened by an Agent Studio hand-off. */
  kind: 'local' | 'client' | 'session';
  /** The client the bearer token belongs to; hosted mode requires it as X-Client-Id on every mutation. */
  clientId: string | null;
  subject: string | null;
  role: 'viewer' | 'operator' | 'owner' | null;
  repositories: string[];
  /** Repeated as X-CSRF-Token on every unsafe request of a hand-off session. */
  csrfToken: string | null;
  federation: { enabled: boolean; studioPublicUrl: string | null };
}

/**
 * The bearer credential for a hosted API.
 *
 * A local API accepts anonymous requests, so an empty token is the normal state and changes
 * nothing. A hosted API answers 401 without one; the interceptor attaches it and reports the
 * rejection so the shell can ask for a token. The value is never logged and never leaves the
 * Authorization header.
 */
@Injectable({ providedIn: 'root' })
export class ApiAccess {
  private readonly http = inject(HttpClient);
  private readonly stored = signal(readToken());
  /** Bumped whenever the API rejects a request as unauthenticated. */
  readonly unauthorizedAt = signal(0);
  /** Bumped when an Agent Studio session ended; the shell reloads, and the host signs in through Studio again. */
  readonly sessionEndedAt = signal(0);
  /** The caller identity the API reported, once something needed it; reset whenever the token changes. */
  readonly session = signal<ApiSession | null>(null);
  readonly token = computed(() => this.stored());
  readonly configured = computed(() => this.stored().length > 0);

  setToken(value: string): void {
    const token = value.trim();
    this.stored.set(token);
    this.session.set(null);
    writeToken(token);
  }

  clear(): void {
    this.stored.set('');
    this.session.set(null);
    writeToken('');
  }

  reportUnauthorized(): void {
    this.unauthorizedAt.update(count => count + 1);
  }

  reportSessionEnded(): void {
    this.sessionEndedAt.update(count => count + 1);
  }

  /** Reads the caller identity. An API without the route (an older host) leaves it null. */
  async loadSession(): Promise<ApiSession | null> {
    const token = this.token();
    try {
      const session = await firstValueFrom(this.http.get<ApiSession>(SESSION_URL));
      if (this.token() === token) this.session.set(session);
      return session;
    } catch {
      return null;
    }
  }
}

function readToken(): string {
  try {
    return localStorage.getItem(TOKEN_STORAGE_KEY) ?? '';
  } catch {
    // Storage can be unavailable (private mode, disabled site data). Anonymous access still works.
    return '';
  }
}

function writeToken(token: string): void {
  try {
    if (token) localStorage.setItem(TOKEN_STORAGE_KEY, token);
    else localStorage.removeItem(TOKEN_STORAGE_KEY);
  } catch {
    // The token then lives for this session only; the request path is unaffected.
  }
}
