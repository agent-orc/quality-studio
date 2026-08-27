---
id: QS-NG-009
title: Every component sets ChangeDetectionStrategy.OnPush
technology: Angular
category: change-detection
kinds: [code]
severity: high
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

Every `@Component` explicitly sets `changeDetection: ChangeDetectionStrategy.OnPush`.
Do not rely on the default (`Default`) strategy.

## Rationale

`OnPush` skips a component's change-detection pass unless one of its
`input()` signals changes, an event handler inside it fires, or a signal it
reads updates — which is what makes signal-based state (`signal`, `computed`)
actually cheap at scale. Leaving a component on the default strategy means
every zone tick re-checks that component's whole template regardless of
whether anything it depends on changed, and mixing strategies in the same
tree makes performance behavior inconsistent and hard to reason about.
Every one of this codebase's eight components (`app`, `review-actions`,
`project-dashboard`, `usage-history`, `attack-coverage`, `review-panel`,
`editor`, `explorer`) already sets `OnPush`; that consistency is worth
protecting deliberately rather than by accident.

## Good example

```ts
// review-panel.ts
@Component({
  selector: 'qs-review-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPanel { /* ... */ }
```

## Bad example

```ts
@Component({
  selector: 'qs-new-feature',
  // no changeDetection set — defaults to CheckAlways, breaking the
  // OnPush-everywhere assumption the rest of the app relies on
})
export class NewFeature { /* ... */ }
```
