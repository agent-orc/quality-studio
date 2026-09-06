---
id: QS-NG-006
version: 1.0.0
title: Address postMessage to a known origin and send only the declared fields
technology: angular
kinds: [security]
category: frame-messaging
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

`postMessage` names the expected recipient origin, taken from trusted configuration or a validated
embedding handshake; `'*'` is not a target origin. The payload carries the fields the contract
declares and nothing else — never a whole `location.href`. Every inbound `message` listener checks
`event.origin` before it reads `event.data`, and validates the shape of what it reads.

## Rationale

`reportUrlPreviewNavigation` types its target origin as the literal `'*'`, so no caller can supply
a real one, and `app.ts` passes it straight to `window.parent.postMessage`: any origin that can
embed the preview receives its navigation messages. The payload compounds it — `url.href` carries
the origin, the fragment, user information, and every pre-existing query parameter, so a token
already in the address bar travels to that unknown parent along with the three fields the contract
actually declares.

## Detection

Search for `postMessage` with `'*'` or a variable that is never compared against an allowlist, for
payloads built from `location.href`, `document.URL`, or a whole `URL` object, and for
`addEventListener('message', ...)` handlers that read `event.data` before checking `event.origin`.
A dedicated Web Worker channel has no origin to check, but still needs its message shape validated.

## Good example

```ts
const parentOrigin = environment.embedOrigin;            // trusted configuration, not the message
if (parentOrigin) {
  window.parent.postMessage(
    { type: 'qs.url-preview.navigate', repo, path, kind },   // only the declared fields
    parentOrigin);
}
```

## Bad example

```ts
// frontend/src/app/url-preview-embed.ts: any embedding origin receives this, href and all
postToParent({ type: 'qs.url-preview.navigate', url: environment.href }, '*');
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the two findings recorded against
  `frontend/src/app/url-preview-embed.ts` for the literal `'*'` target and the whole-href payload.
