---
id: QS-NG-009
version: 1.0.0
title: Build URLs by encoding parts, and never navigate to a URL from data
technology: angular
kinds: [security]
category: url-handling
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

Path segments go through `encodeURIComponent` and query values through `HttpParams` or
`URLSearchParams`; a URL is never assembled by concatenating raw values. A navigation target — a
`[href]`, `router.navigateByUrl`, `window.open`, or an assignment to `location` — is a same-origin
route this application computed, never a URL that arrived in a response, a query parameter, or a
message.

## Rationale

`quality-api.ts` escapes every id it puts in a path and passes every value as a parameter, so a
repository or finding id containing a slash or an ampersand stays one segment instead of becoming a
new path or a new parameter, and every request stays same-origin and relative. The navigation half
is what turns a formatting bug into an open redirect: a link target taken from data sends the user,
and the referrer, wherever that data says.

## Detection

Look for template strings that interpolate an id or a user value into a URL without
`encodeURIComponent`, for query strings built with `+` or interpolation instead of `HttpParams`,
and for `[href]`, `window.open`, `location.href =`, or `navigateByUrl` bound to a value from a
response, a route parameter, or a message. A relative URL built from constants is not a violation.

## Good example

```ts
// frontend/src/app/quality-api.ts
private repositoryApiBase(): string {
  return this.legacyApi ? '/api' : `/api/repos/${encodeURIComponent(repositoryId)}`;
}
const file = await firstValueFrom(
  this.http.get<FileDocument>(`${this.repositoryApiBase()}/file`, { params: { path } }));
```

## Bad example

```ts
// An id with a slash becomes a new path segment; the link target comes from the response.
this.http.get(`/api/repos/${repositoryId}/file?path=` + path);
window.open(response.externalUrl, '_blank');
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the escaped path segments and `HttpParams` usage
  throughout `frontend/src/app/quality-api.ts`.
