---
id: QS-NG-005
title: Declare OnPush change detection on every component
summary: Every component must set changeDetection to ChangeDetectionStrategy.OnPush; default (dirty-checked) change detection is a finding.
technology: angular
category: change-detection
severity: high
autofixable: false
defaultOn: true
kinds: [code]
levels: [file]
version: 1.0.0
status: active
deterministicCheck: {tool: eslint, ruleId: "@angular-eslint/prefer-on-push-component-change-detection"}
---
## Statement

Every `@Component` decorator MUST set
`changeDetection: ChangeDetectionStrategy.OnPush`. Leaving the default
strategy is a finding, not a style nit: default change detection dirty-checks
the whole tree on every event and defeats the signal-based reactivity the
rest of the codebase relies on.

## Rationale

Mixing default and `OnPush` components in the same tree is worse than an
all-default tree: it hides the exact bug class `OnPush` exists to prevent
(a child that doesn't re-render because its inputs changed by mutation, not
reference) while still paying the dirty-checking cost everywhere else. Since
every reviewed component already uses `input()`/`signal()`, the immutable
data flow `OnPush` requires is already the codebase's norm — declaring it is
free.

## Good example

Every component in Quality Studio's frontend already does this, e.g.
`frontend/src/app/app.ts:39` and `frontend/src/app/review-panel/review-panel.ts:20`:

```ts
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  ...
})
```

## Bad example

```ts
@Component({
  selector: 'qs-widget',
  templateUrl: './widget.html',
  // no changeDetection: defaults to ChangeDetectionStrategy.Default
})
export class Widget { ... }
```

## Notes

Agent Studio enforces this deterministically with
`'@angular-eslint/prefer-on-push-component-change-detection': 'error'`
(`frontend/eslint.config.js:25`). Marked `autofixable: false`: adding the
property is one line, but it can change what re-renders, so the change
still needs a human to confirm nothing relied on mutation-based dirty
checking.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in the Quality Studio and Agent
  Studio frontends during the QS-90 rule-library seeding session.
