---
id: QS-NG-012
version: 1.0.0
title: Keep new weight out of the initial bundle
technology: angular
kinds: [performance]
category: bundle-budget
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

A feature that is not on the first screen loads as a deferred chunk (`@defer`, a lazy route)
rather than as an eager import, and a new dependency is weighed against the production budgets in
`frontend/angular.json` before it is added. Component stylesheets stay inside the per-stylesheet
budget instead of accumulating one-off blocks.

## Rationale

The production build errors, not warns, when the initial bundle passes its limit: a build has
already failed at 494.94 kB against the 480 kB error budget, so an eager import of a rarely used
feature does not degrade the app gradually — it stops the build for whoever commits next. The
attack matrix shows the intended shape, a deferred 17 kB chunk that costs the first screen nothing.

## Detection

Check whether a newly imported component, library, or icon set is reachable from the initial route
and whether a heavy feature is declared with `@defer` or a lazy route. Compare added stylesheet
weight against the `anyComponentStyle` budget in `frontend/angular.json` — the authoritative
numbers are there, not in prose that may have drifted from it.

## Good example

```html
<!-- A rarely opened, heavy pane costs the first screen nothing -->
@defer (on interaction) {
  <app-attack-coverage />
} @placeholder {
  <button class="pane-header">Attack coverage</button>
}
```

## Bad example

```ts
// Eagerly imported into the shell, so it is in the initial bundle for everyone
import { AttackCoverage } from './attack-coverage/attack-coverage';
import * as everything from 'some-charting-library';
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the production budgets in `frontend/angular.json`
  and the build failure at 494.94 kB recorded in `docs/operations/static-analysis/`.
