---
id: QS-NG-003
title: Reuse standard components and shared UI primitives
language: angular
severity: medium
autofixable: false
version: 1.0.0
status: active
kinds: [code]
levels: [file, module, project]
applies-to: [.ts, .html]
references: [frontend/src/styles.css, frontend/DESIGN-KINSHIP.md, Agent Studio frontend/AGENTS.md]
deterministic-check: none
---

## Statement

Before creating local markup or styles for a recurring control, row, badge, pane, card, dialog, or navigation pattern, reuse the repository's standard component or shared primitive. Extend the shared primitive when the same semantics will have multiple consumers.

## Rationale

Agent Studio provides canonical components such as tree rows, section headers, count badges, task reference microcards, and side sheets. Quality Studio provides shared workbench primitives such as pane headers, status signals, and finding controls. Reimplementation forks accessibility, interaction, theme, and density behavior.

## Bad example

```html
<span class="local-count-pill">{{ count() }}</span>
```

```css
.local-count-pill { padding: 2px 7px; border-radius: 999px; }
```

## Good example

```html
<app-count-badge [count]="count()" />
```

Or, in Quality Studio, apply the existing shared primitive whose semantics match the surface instead of cloning its geometry.

## Change history

- 2026-08-12, v1.0.0: Initial rule covering the operator-observed lack-of-component-reuse defect class.

