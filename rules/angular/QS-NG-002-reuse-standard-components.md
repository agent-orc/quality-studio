---
id: QS-NG-002
version: 1.0.0
title: Reuse standard components and shared primitives
technology: angular
category: component-reuse
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.0.0
---

## Statement

Before adding a new badge, panel, or control markup pattern, check `frontend/src/styles.css`
and sibling feature folders for an existing shared primitive (e.g. `.severity`, `.pane`,
`.pane-header`) or a reusable standalone component. Extend or reuse it instead of writing a
parallel one-off implementation with its own markup and styling.

## Rationale

`frontend/src/styles.css` already documents this as an explicit convention: "Shared workbench
primitives reused across shell panes (Explorer, Editor, ReviewPanel)." Duplicated one-off
primitives drift from each other over time (spacing, states, accessibility), double the
maintenance surface, and are exactly the failure mode design tokens alone cannot prevent — a
component can use tokens correctly and still reinvent a pattern that already exists.

## Good example

```html
<!-- frontend/src/app/review-panel/review-panel.html: reuses the shared .severity primitive -->
<span class="severity" [class]="'severity ' + finding.severity">{{ finding.severity }}</span>
```

## Bad example

```html
<!-- a new feature reimplements its own severity chip instead of reusing .severity -->
<span class="status-chip" [ngStyle]="{ background: severityColor(finding.severity) }">
  {{ finding.severity }}
</span>
```

## Change history

- 1.0.0 (2026-08-27): Initial rule. Directly covers the operator-observed defect class
  ("ad-hoc styles instead of design tokens / no component reuse"), paired with QS-NG-001.
