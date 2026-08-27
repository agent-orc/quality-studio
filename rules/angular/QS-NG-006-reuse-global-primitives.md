---
id: QS-NG-006
title: Reuse existing global style primitives before defining parallel ones
technology: Angular
category: component-reuse
kinds: [code]
severity: medium
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

Before adding a new CSS class for a common element (an icon button, a status
dot, a card surface), check `styles.css` for an existing global primitive
(`.icon-button`, `.status`, `.pane`, `.pane-header`) that already covers the
case, and reuse or extend it instead of defining a new class with the same
purpose in a feature stylesheet.

## Rationale

`styles.css` explicitly documents this intent — its comment above `.pane`
reads "Shared workbench primitives reused across shell panes (Explorer,
Editor, ReviewPanel)" — but the follow-through is inconsistent: `app.css`
defines its own `.primary-button`/`.secondary-button`/`.danger-button` set
with their own height, padding, and border-radius values rather than
building on `.icon-button`'s existing 29px control sizing, and several
feature files re-declare "a small rounded control" (`.risk-matrix span`,
`.hotspot-row`) with one-off dimensions instead of the token-based
`--studio-control-hit-compact` / `--studio-control-sm` sizes already used
elsewhere for exactly that purpose.

## Good example

```css
/* styles.css already defines the compact control sizing token */
.review-pane .finding-controls select,
.review-pane .finding-controls button {
  min-height: var(--studio-control-hit-compact);
}
```

## Bad example

```css
/* app.css defines a new, differently-sized button family instead of
   extending .icon-button / the existing control-height tokens */
.primary-button, .secondary-button, .danger-button {
  height: 32px;
  padding: 0 12px;
  border-radius: 5px;
}
```
