---
id: QS-NG-008
title: Prefer signals and host bindings over manual subscribe()/addEventListener
technology: Angular
category: template-hygiene
kinds: [code]
severity: medium
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

When a component needs to react to a DOM or observable event, prefer a
declarative `host` binding or an Angular primitive (`toSignal`, `effect`)
over a manual `addEventListener`/`.subscribe()` call in the constructor or
`ngOnInit`. If a manual subscription is genuinely required, it must be torn
down (an `AbortController`, a `DestroyRef`-scoped cleanup, or the
`host`-binding form that Angular tears down itself).

## Rationale

A manual subscription that isn't paired with explicit teardown leaks a
listener every time the component is created and destroyed — a slow memory
and event-storm leak that is invisible in a quick manual test and only shows
up under sustained use. Angular's `host` binding form (used by
`ReviewActions` for outside-click detection) is torn down automatically with
the component, which removes the failure mode entirely rather than relying
on every future editor of the file remembering to clean up.

## Good example

```ts
// review-actions.ts — host binding, torn down automatically with the component
@Component({
  selector: 'qs-review-actions',
  host: { '(document:pointerdown)': 'closeOnOutsidePointer($event)' },
})
export class ReviewActions { /* ... */ }
```

## Bad example

```ts
export class ReviewActions {
  constructor() {
    document.addEventListener('pointerdown', this.closeOnOutsidePointer.bind(this));
    // never removed: this listener outlives every destroyed instance of the component
  }
}
```
