import { HttpErrorResponse } from '@angular/common/http';

import { FileError, FileErrorKind } from './contracts';

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

function fileErrorKind(status: number | null): FileErrorKind {
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
  const kind = fileErrorKind(status);
  const template = FILE_ERRORS[kind];
  const serverDetail = error instanceof HttpErrorResponse
    ? (typeof error.error?.detail === 'string' ? error.error.detail : null)
    : null;
  return {
    path,
    kind,
    status,
    title: template.title,
    detail: serverDetail ?? template.detail,
    retryable: template.retryable,
  };
}
