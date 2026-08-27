---
id: QS-NG-010
title: Drive updates through signals, not manual markForCheck()/detectChanges()
technology: Angular
category: change-detection
kinds: [code]
severity: medium
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

In application code (not test specs), update component state through
`signal.set()`/`signal.update()` and let Angular's `OnPush` dependency
tracking re-render, instead of calling `ChangeDetectorRef.markForCheck()` or
`ApplicationRef.tick()` from an async callback that mutates a plain field.

## Rationale

`markForCheck()`/`detectChanges()` calls from application code are almost
always a symptom that some state update is happening outside Angular's
reactivity graph (a plain class field mutated inside a `setTimeout` or a
non-Angular event callback), which is exactly what `OnPush` plus signals is
meant to make unnecessary — the fix is to make the state itself a signal, not
to manually force a re-render around it. This codebase currently has zero
`markForCheck`/`detectChanges` calls in application code (`fixture.detectChanges()`
appears only in `*.spec.ts` test files, which is the correct, expected use in
a `TestBed` harness); a new manual call in a component or service is a
regression against that baseline, not a neutral addition.

## Good example

```ts
// review-actions.ts — async state flows through a signal; OnPush re-renders itself
readonly starting = signal(false);

async startReview() {
  this.starting.set(true);
  await this.api.startReview(request);
  this.starting.set(false);
}
```

## Bad example

```ts
export class ReviewActions {
  starting = false;
  constructor(private readonly cdr: ChangeDetectorRef) {}

  async startReview() {
    this.starting = true;
    this.cdr.markForCheck();
    await this.api.startReview(request);
    this.starting = false;
    this.cdr.markForCheck();
  }
}
```
