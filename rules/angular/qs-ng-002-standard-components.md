---
id: QS-NG-002
title: Reuse a standard component instead of a one-off UI primitive
summary: Recurring UI primitives (buttons, badges, panels, tabs) must live in a shared component/mixin, not be redeclared per feature.
technology: angular
category: standard-component-reuse
severity: medium
autofixable: false
defaultOn: true
kinds: [code]
levels: [file, module]
version: 1.0.0
status: active
---
## Statement

Before adding a new button, badge, panel, or other recurring UI primitive,
check whether an equivalent already exists in the shared component/style
library. If it does, reuse or extend it. If the same primitive is being
redeclared per feature folder with copy-pasted markup or CSS, extract it once
instead of adding another copy.

## Rationale

A UI primitive that only exists as a repeated CSS class definition (rather
than a real shared Angular component, or at minimum a shared mixin) has no
single point of change: fixing an accessibility issue, a hover state, or a
spacing bug in one copy does not fix the others, and the copies silently
drift apart. This is the second half of the operator-observed defect class:
ad-hoc styles *and* no component reuse.

## Good example

Agent Studio's `frontend/src/app/components/` holds 41 shared, standalone
components (`count-badge`, `pane-header`, `pane-tabs`, `dialog`, `sidesheet`,
`tree-row`, `tooltip`, `menu`, `empty-state`, ...) that features import and
compose rather than reimplementing. Where a full component would be overkill,
recurring style patterns are still centralized as SCSS mixins in
`frontend/src/styles/_mixins.scss` (`@mixin icon-button`, `@mixin chip`,
`@mixin deck-panel`) and consumed with `@include m.deck-panel;`.

## Bad example

Quality Studio's frontend has no `shared/`/`components/` directory. The
`.icon-button` and `.secondary-button` primitives are instead redeclared with
independent styles in two places:

`frontend/src/app/app.css:34`:

```css
.primary-button, .secondary-button, .danger-button {
  height: 32px; padding: 0 12px; border: 1px solid var(--studio-border); border-radius: 5px;
}
```

`frontend/src/app/attack-coverage/attack-coverage.css:5`:

```css
.secondary-button { height: 32px; padding: 0 12px; border: 1px solid var(--studio-border); ... }
.icon-button { display: grid; place-items: center; width: 30px; height: 30px; ... }
```

Both class names are reused by name across `app.html`, `attack-coverage.html`,
and `usage-history.html`, but the two CSS definitions above are independent:
editing one does not edit the other, and there is no compiler error to catch
the drift.

## Recommendation

Extract a shared Angular component (e.g. `<qs-button variant="secondary">`)
or, at minimum, a single shared stylesheet/mixin these files `@import`, so the
primitive has exactly one definition.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in the Quality Studio and Agent
  Studio frontends during the QS-90 rule-library seeding session.
