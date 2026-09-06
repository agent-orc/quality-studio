import { Injectable, computed, signal } from '@angular/core';

const TOKEN_STORAGE_KEY = 'qs-api-token';

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
  private readonly stored = signal(readToken());
  /** Bumped whenever the API rejects a request as unauthenticated. */
  readonly unauthorizedAt = signal(0);
  readonly token = computed(() => this.stored());
  readonly configured = computed(() => this.stored().length > 0);

  setToken(value: string): void {
    const token = value.trim();
    this.stored.set(token);
    writeToken(token);
  }

  clear(): void {
    this.stored.set('');
    writeToken('');
  }

  reportUnauthorized(): void {
    this.unauthorizedAt.update(count => count + 1);
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
