---
id: QS-NG-004
version: 1.0.0
title: Keep templates declarative; always track list expressions
technology: angular
category: template-hygiene
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.0.0
---

## Statement

Templates use the built-in control-flow blocks (`@for`, `@if`, `@empty`) with an explicit
`track` expression on every `@for`, and call only simple, side-effect-free reads (signals,
plain fields) directly in the template. Non-trivial derivation belongs in a `computed()` or a
named method on the component, not inlined as a template expression.

## Rationale

`track` is what lets Angular reuse DOM nodes across re-renders instead of tearing down and
rebuilding a list on every change; omitting it silently degrades to identity-based tracking
and defeats `OnPush`'s benefit. Pushing derivation into the template makes it invisible to
unit tests and re-evaluated on every check, whereas a `computed()` is both testable and memoized.

## Good example

```html
<!-- frontend/src/app/review-panel/review-panel.html -->
@for (finding of visibleFindings(); track finding.fingerprint ?? finding.id) {
  <button class="finding-card" (click)="selectFindingLocation(finding)">...</button>
} @empty {
  <div class="empty-findings">No findings match these filters.</div>
}
```

## Bad example

```html
<!-- missing track, and re-sorting inline on every check -->
@for (finding of findings().slice().sort((a, b) => a.severity.localeCompare(b.severity))) {
  <button class="finding-card">...</button>
}
```

## Change history

- 1.0.0 (2026-08-27): Initial rule, grounded in the `track finding.fingerprint ?? finding.id`
  pattern already used throughout `review-panel.html`.
