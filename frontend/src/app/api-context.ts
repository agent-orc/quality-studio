import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';

import { describeHttpError, isUnreachable } from './api-errors';
import { ApiConnectionState } from './contracts';

/**
 * What every API service needs to agree on: which repository is selected, where its routes live,
 * whether the API is answering, and how a failure reads. Holding it in one place is what lets the
 * domain services (repositories, runs, findings, scope) exist side by side without knowing about
 * each other.
 */
@Injectable({ providedIn: 'root' })
export class ApiContext {
  /** True once a request found a server without the repository registry, which serves /api/... flat. */
  readonly legacyApi = signal(false);
  readonly selectedRepositoryId = signal('default');
  readonly connectionState = signal<ApiConnectionState>('connecting');
  readonly connectionError = signal('');
  readonly connected = computed(() => this.connectionState() === 'live');
  /** True while the shell shows labelled demonstration data because the API is unreachable. */
  readonly preview = computed(() => this.connectionState() === 'preview');
  readonly connectionLabel = computed(() => {
    const state = this.connectionState();
    return state === 'live'
      ? 'Repository connected'
      : state === 'preview'
        ? 'API offline, preview data'
        : state === 'offline'
          ? 'API offline'
          : 'Connecting to API';
  });

  repositoryApiBase(repositoryId = this.selectedRepositoryId()): string {
    return this.legacyApi() ? '/api' : `/api/repos/${encodeURIComponent(repositoryId)}`;
  }

  /** True when the request never reached the API, so no server-side judgement exists. */
  unreachable(error: unknown): boolean { return isUnreachable(error); }

  errorMessage(error: unknown): string { return describeHttpError(error); }

  /** A conditional request answered 304: the retained snapshot is still current. */
  notModified(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 304;
  }
}
