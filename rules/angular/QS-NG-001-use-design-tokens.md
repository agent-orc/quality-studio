---
id: QS-NG-001
version: 1.1.0
title: Use design tokens, not raw values
technology: angular
kinds: [code]
category: design-tokens
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.0.0
---

## Statement

Component styles must reference the central design-token custom properties (`--studio-*`,
`--space-*`, `--font-*`, `--syntax-*`, etc., defined once in `frontend/src/styles.css`) for
color, spacing, radius, and typography. Do not hard-code hex colors, raw pixel values, or
one-off font sizes in a component's own `.css` file.

## Rationale

A single token source is what lets the whole app re-theme (light/dark, `data-theme`) and stay
visually consistent without hunting through every feature folder. Every raw value a component
invents is a value the token system and future theme changes cannot see or move together.

## Detection

Read the component's `.css` for literal colors (`#rrggbb`, `rgb(`, named colors), raw `px`/`rem` lengths on padding, margin, gap, `border-radius`, and `font-size`, and for shadows written out by hand. A value is a violation when an equivalent `--studio-*`, `--space-*`, or `--font-*` token exists in `frontend/src/styles.css`; `0`, `1px` hairlines, and percentage/`fr` layout values are not.

## Good example

```css
/* frontend/src/app/review-panel/review-panel.css */
.severity {
  padding: var(--studio-space-1) var(--studio-space-2);
  border-radius: var(--studio-radius-badge);
  background: var(--studio-bg-elevated);
  font-size: var(--studio-font-size-label);
}
.severity.critical { color: var(--studio-severity-critical); }
```

## Bad example

```css
/* a new feature invents its own palette and spacing instead of reusing tokens */
.priority-tag {
  padding: 4px 8px;
  border-radius: 4px;
  background: #eef1f5;
  font-size: 11px;
  color: #991b1b;
}
```

## Change history

- 1.1.0 (2026-09-06): Declared the applicable review kinds and added detection guidance for the generated catalogue.
- 1.0.0 (2026-08-27): Initial rule, grounded in the `--studio-*` token scale already defined in
  `frontend/src/styles.css` and consumed by `review-panel.css`.