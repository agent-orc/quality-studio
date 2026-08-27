---
id: QS-NG-003
title: Keep components single-purpose; extract growing concerns
summary: A component that keeps absorbing unrelated concerns (layout mechanics, form state, routing) should shed them to child components or services.
technology: angular
category: component-structure
severity: medium
autofixable: false
defaultOn: false
kinds: [code]
levels: [file]
version: 1.0.0
status: active
---
## Statement

A component's constructor/`inject()` list and template should describe one
coherent responsibility. When a component accretes multiple unrelated
concerns over time — window-level event handling, drag/resize mechanics,
domain CRUD forms, and routing — extract the concerns that do not depend on
each other into child components, directives, or injectable services before
adding the next one.

## Rationale

A large root/container component is easy to create incrementally: every new
feature has one obvious place to add "just one more" method. But it also
becomes the one file every contributor touches, the one component nobody can
safely refactor because its concerns are entangled, and the one place a
change-detection regression is hardest to isolate. Splitting by concern keeps
each piece independently testable and reviewable.

## Good example

Quality Studio's own feature views are appropriately scoped: `editor.ts`,
`explorer.ts`, `review-panel.ts`, and `usage-history.ts` each own one pane's
concern and communicate with their parent through `input()`/`output()`
signals rather than reaching into shared mutable state.

## Bad example

`frontend/src/app/app.ts` (576 lines) is the composition root for every one
of those panes, and additionally owns: window resize/keydown/drag-to-resize
handling (`host: { '(window:resize)': ... }`), workspace layout persistence
(`LAYOUT_STORAGE_KEY`), repository registration, guideline CRUD form state
(`GuidelineForm`), and finding-route read/write. None of the layout-drag
mechanics or the guideline form depend on each other; both are candidates to
extract (a `ResizablePaneDirective`/layout service, and a
`GuidelineEditor` child component) rather than growing `app.ts` further.

## Recommendation

When a component's `inject()` list mixes unrelated domains, or its template
mixes layout chrome with feature markup, treat that as a prompt to extract
before the component grows again — not a one-time cleanup deferred
indefinitely.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in the Quality Studio frontend
  during the QS-90 rule-library seeding session.
