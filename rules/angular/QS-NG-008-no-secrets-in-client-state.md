---
id: QS-NG-008
version: 1.0.0
title: Keep credentials out of client-held state
technology: angular
kinds: [security]
category: client-state
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

`localStorage`, `sessionStorage`, IndexedDB, cookies written from script, the URL, and component
state hold non-secret presentation state only — a theme, a layout, a selected filter. Tokens,
credentials, and authorization headers are not constructed, stored, or logged in the browser.

## Rationale

The Angular client stores exactly two things, `'qs-theme'` and the layout key, and builds no
`Authorization` header at all: authentication is a bearer token and a client id enforced by
`ApiSecurity` on the server, which is why nothing that leaks from the browser — a shared device, a
console log, an embedding parent — is a credential. Client-held storage is readable by any script
on the origin and survives the session, so a token placed there outlives the reason it was issued.

## Detection

Look at every `localStorage`/`sessionStorage` key and every value assigned to a header, a query
parameter, or a logged object for names like token, key, secret, password, or authorization, and
for a credential arriving in a route parameter or fragment. A non-secret repository id or view
preference in storage is not a violation.

## Good example

```ts
// frontend/src/app/app.ts
localStorage.setItem('qs-theme', theme);          // presentation state only
localStorage.setItem(LAYOUT_STORAGE_KEY, JSON.stringify(sizes));
```

## Bad example

```ts
localStorage.setItem('qs-api-token', token);      // readable by any script on this origin, forever
this.http.get(url, { headers: { Authorization: `Bearer ${token}` } });
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the two non-secret storage keys the client keeps
  and the server-side placement of authentication in `ApiSecurity`.
