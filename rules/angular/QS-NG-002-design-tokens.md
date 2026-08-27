---
id: QS-NG-002
version: 1.0.0
title: Use design tokens instead of ad-hoc visual literals
language: angular
kinds: [code]
appliesTo: [**/*.css, **/*.scss, **/*.html]
severity: high
defaultOn: true
autofixable: false
deterministic: true
---

## Statement

Use the shared design-token scale for spacing, color, typography, radii, shadows, and control geometry; do not introduce raw pixel values or color literals in feature styles when a central token expresses the intent.

## Rationale

Token use keeps Agent Studio and Quality Studio visually coherent across light and dark themes and prevents small one-off values from creating an unmaintainable parallel design system. Agent Studio's `--studio-spacing-*` scale and Quality Studio's `--studio-space-*` aliases are the source of spacing truth; a new token belongs in the central token definition before feature code consumes it.

## Bad example

```css
.finding-card {
  padding: 13px;
  border-radius: 7px;
  background: #26364f;
}
```

## Good example

```css
.finding-card {
  padding: var(--studio-space-3);
  border-radius: var(--studio-radius-card);
  background: var(--studio-bg-elevated);
}
```

## Change history

- 2026-08-12: Initial default-on rule; covers the operator-observed ad-hoc-style defect class.
