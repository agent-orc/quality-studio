import { HttpErrorResponse } from '@angular/common/http';

import { FileError, FileErrorKind } from './contracts';

/** Raised by the API interceptor when a request exceeds its budget without any response. */
export class ApiTimeoutError extends Error {
  constructor(readonly url: string, readonly timeoutMs: number) {
    super(`The API did not answer within ${Math.round(timeoutMs / 1000)} s.`);
    this.name = 'ApiTimeoutError';
  }
}

/** True when the request produced no server judgement at all: no response, or no response in time. */
export function isUnreachable(error: unknown): boolean {
  return error instanceof ApiTimeoutError || (error instanceof HttpErrorResponse && error.status === 0);
}

const STATUS_MESSAGES: Record<number, string> = {
  400: 'The API rejected the request as invalid.',
  401: 'The API rejected the request as unauthenticated. Configure an API access token.',
  403: 'The configured credentials are not permitted to perform this request.',
  404: 'The API has nothing at this address.',
  409: 'The API refused the change because the underlying state moved on. Reload and try again.',
  413: 'The request or its response exceeded the size the API accepts.',
  429: 'The API is rate limiting requests. Wait a moment and try again.',
};

function serverDetail(error: HttpErrorResponse): string | null {
  const body = error.error as { detail?: unknown; title?: unknown } | string | null;
  if (typeof body === 'string' && body.trim() && !body.trim().startsWith('<')) return body.trim();
  if (body && typeof body === 'object') {
    if (typeof body.detail === 'string' && body.detail.trim()) return body.detail.trim();
    if (typeof body.title === 'string' && body.title.trim()) return body.title.trim();
  }
  return null;
}

/**
 * The single place that turns a transport failure into a sentence for a reader. Angular's own
 * "Http failure response for /api/...: 500 Internal Server Error" never reaches the interface.
 */
export function describeHttpError(error: unknown): string {
  if (error instanceof ApiTimeoutError) {
    return `${error.message} The request was cancelled; the work may still be running on the server.`;
  }
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) return 'The API is not reachable. Check that the Quality Studio API is running.';
    const detail = serverDetail(error);
    if (detail) return detail;
    if (error.status >= 500) return `The API reported an internal error (HTTP ${error.status}).`;
    return STATUS_MESSAGES[error.status] ?? `The API answered with HTTP ${error.status}.`;
  }
  return error instanceof Error ? error.message : 'The repository request failed.';
}

const FILE_ERRORS: Record<FileErrorKind, { title: string; detail: string; retryable: boolean }> = {
  unauthorized: {
    title: 'Sign-in required',
    detail: 'The API rejected the request as unauthenticated. Configure an API access token and try again.',
    retryable: false,
  },
  forbidden: {
    title: 'Access denied',
    detail: 'The configured credentials may not read this file. A token with repository read access is required.',
    retryable: false,
  },
  'out-of-scope': {
    title: 'Not in the review scope',
    detail: 'The API has no document for this path. It may be excluded by a scope rule, or removed from the working tree.',
    retryable: false,
  },
  'too-large': {
    title: 'File too large to open',
    detail: 'The API refused to transfer this document because of its size. Review it in the repository instead.',
    retryable: false,
  },
  unavailable: {
    title: 'File could not be loaded',
    detail: 'The API did not deliver this document. The request can be repeated.',
    retryable: true,
  },
};

function fileErrorKind(error: unknown, status: number | null): FileErrorKind {
  if (error instanceof ApiTimeoutError) return 'unavailable';
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (status === 404 || status === 410) return 'out-of-scope';
  if (status === 413 || status === 414 || status === 416) return 'too-large';
  return 'unavailable';
}

/**
 * Maps a failed file request onto the state the editor renders. Status-aware by design: the reader
 * must be able to tell "you may not see this" from "it is not reviewed" from "try again".
 */
export function describeFileError(path: string, error: unknown): FileError {
  const status = error instanceof HttpErrorResponse ? error.status : null;
  const kind = fileErrorKind(error, status);
  const template = FILE_ERRORS[kind];
  const detail = error instanceof HttpErrorResponse ? serverDetail(error) : null;
  return {
    path,
    kind,
    status,
    title: template.title,
    detail: detail ?? (error instanceof ApiTimeoutError ? error.message : template.detail),
    retryable: template.retryable || error instanceof ApiTimeoutError,
  };
}
