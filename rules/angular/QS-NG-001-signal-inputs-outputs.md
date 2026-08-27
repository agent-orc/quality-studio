---
id: QS-NG-001
title: Declare component I/O with the signal input()/output() functions
technology: Angular
category: component-structure
kinds: [code]
severity: low
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

Declare a component's inputs and outputs with the `input()`/`input.required()`
and `output()` functions, not the `@Input()`/`@Output()` decorators.

## Rationale

Signal inputs are readable as plain signals (composable with `computed()` and
`effect()` without a manual `ngOnChanges`), make "required" a compile-time
property via `input.required<T>()` instead of a runtime convention, and avoid
the decorator-plus-`EventEmitter` boilerplate. Mixing both styles in the same
codebase forces every reader to check which convention a given component
uses before they can safely consume it.

## Good example

```ts
// review-actions.ts
export class ReviewActions {
  readonly node = input<TreeNode | undefined>();
  readonly activeKind = input.required<ReviewKind>();
  readonly compact = input(false);
  readonly kindSelect = output<ReviewKind>();
}
```

## Bad example

```ts
export class ReviewActions {
  @Input() node?: TreeNode;
  @Input() activeKind!: ReviewKind;
  @Input() compact = false;
  @Output() kindSelect = new EventEmitter<ReviewKind>();
}
```
