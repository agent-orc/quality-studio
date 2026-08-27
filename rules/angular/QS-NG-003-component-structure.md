---
id: QS-NG-003
version: 1.0.0
title: One focused standalone component per feature folder
technology: angular
category: component-structure
severity: low
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.0.0
---

## Statement

Keep each feature as one standalone component with its own folder holding exactly its
`.ts`, `.html`, `.css`, and `.spec.ts` (the pattern already used by `review-panel/`,
`explorer/`, `attack-coverage/`, etc.). Do not fold unrelated feature concerns into an
existing component or split a single feature's template across multiple ad-hoc components
without a clear ownership boundary.

## Rationale

The one-feature-one-folder convention is what makes the app navigable: a reviewer can find
`review-panel.ts`/`.html`/`.css`/`.spec.ts` together and reason about the whole feature. Mixing
concerns into a component that already has a name and a job (or splintering one feature across
several loosely related components) erodes that mapping and makes change detection, testing,
and template hygiene reviews harder because no single file is "the" source of truth anymore.

## Good example

```
frontend/src/app/review-panel/
  review-panel.ts
  review-panel.html
  review-panel.css
  review-panel.spec.ts
```

## Bad example

```
frontend/src/app/review-panel/
  review-panel.ts        // now also renders the project dashboard's summary cards
  review-panel.html
  review-panel.css
  dashboard-bits.ts       // half of a second, unrelated feature bolted on here
```

## Change history

- 1.0.0 (2026-08-27): Initial rule, grounded in the existing `frontend/src/app/*` feature-folder
  layout (`review-panel`, `explorer`, `attack-coverage`, `usage-history`, `editor`,
  `project-dashboard`, `review-actions`).
