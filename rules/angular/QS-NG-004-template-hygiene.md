---
id: QS-NG-004
version: 1.0.0
title: Keep templates declarative and presentation-free
language: angular
kinds: [code]
appliesTo: [**/*.html, **/*.ts]
severity: medium
defaultOn: true
autofixable: false
deterministic: true
---

## Statement

Keep Angular templates declarative: use external template and style files, bind semantic state and shared classes, avoid inline styles and style bindings, and move non-trivial calculations or side effects into typed component state.

## Rationale

Declarative templates are easier to scan, test, theme, and review. Class-based state works with the central token system, while repeated expressions and inline presentation hide behavior in markup and bypass component styling conventions.

## Bad example

```html
<div [style.width.px]="score() * 2" style="margin-top: 7px">
  {{ expensiveSummary(run()) }}
</div>
```

## Good example

```html
<div class="score-track" [class.complete]="isComplete()">
  {{ summary() }}
</div>
```

## Change history

- 2026-08-12: Initial default-on rule with deterministic checks for inline presentation bindings and inline component template/style metadata.
