---
id: QS-NG-003
version: 1.0.0
title: Reuse standard components before adding UI primitives
language: angular
kinds: [code]
appliesTo: [**/*.ts, **/*.html, **/*.css, **/*.scss]
severity: high
defaultOn: true
autofixable: false
deterministic: false
---

## Statement

Before creating a local button, badge, status marker, panel header, dialog, tab, or conversation primitive, reuse or extend the established Studio component or shared class that already owns that interaction and appearance.

## Rationale

Standard-component reuse preserves accessibility behavior, keyboard interactions, states, and visual consistency. Agent Studio's `app-tree-row`, `app-section-header`, `app-count-badge`, `app-list-row`, and `cac-chat` surfaces and Quality Studio's pane headers, icon buttons, status markers, and review actions already own common behavior. Duplicating their markup and geometry creates drift and multiplies fixes.

## Bad example

```html
<span class="local-fresh-dot"></span>
<button class="tiny-square-button">×</button>
```

## Good example

```html
<app-tree-row [label]="project.name" [active]="selected()" />
<app-count-badge [count]="project.openTasks" />
```

## Change history

- 2026-08-12: Initial default-on rule; covers the operator-observed no-component-reuse defect class.
