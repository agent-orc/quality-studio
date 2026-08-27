---
id: QS-NG-004
title: Space and round with the spacing/radius token scale
technology: Angular
category: design-tokens
kinds: [code]
severity: medium
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

Use `var(--studio-space-1..4)` (or `--space-1..6`) for padding, margin, and
gap, and `var(--studio-radius-sm|md|lg|badge|card|pill)` for border-radius,
instead of raw pixel literals.

## Rationale

`styles.css` defines a small, deliberate spacing and radius scale
(`--studio-space-1: 4px` through `--studio-space-4: 16px`;
`--studio-radius-sm/md/lg/badge/card/pill`) precisely so spacing stays
consistent across panes without every component picking its own numbers.
Several existing stylesheets already do this correctly (`.finding-detail`,
`.scope-manager` in `styles.css` use `var(--studio-space-*)` throughout), but
newer feature CSS reverts to raw pixels — e.g. `app.css` and
`attack-coverage.css` mix `padding: 9px`, `border-radius: 5px` alongside
token-based rules in the same file. A raw `7px` next to a token-based `8px`
two rules later is not a deliberate design choice, it is drift.

## Good example

```css
/* styles.css, .finding-detail */
.review-pane .finding-detail {
  margin: var(--studio-space-3) 0;
  padding: var(--studio-space-4);
  border-radius: var(--studio-radius-card);
}
```

## Bad example

```css
/* app.css:33, raw pixels alongside token-based rules elsewhere in the same file */
.form-error {
  padding: 9px;
  border-radius: 5px;
}
```
