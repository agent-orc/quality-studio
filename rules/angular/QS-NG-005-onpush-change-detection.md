---
id: QS-NG-005
version: 1.0.0
title: Use OnPush change detection with reactive state
language: angular
kinds: [code, performance]
appliesTo: [**/*.ts]
severity: medium
defaultOn: true
autofixable: false
deterministic: false
---

## Statement

Feature components must use `ChangeDetectionStrategy.OnPush` and expose view state through signals, inputs, outputs, or observables without relying on incidental mutable-field detection.

## Rationale

Quality Studio's interactive explorer and editor need predictable rendering costs. OnPush plus explicit reactive state makes update ownership visible, avoids broad change-detection work, and prevents mutations that silently fail to update a view.

## Bad example

```ts
@Component({ selector: 'qs-tree', templateUrl: './tree.html' })
export class TreeComponent {
  selected = '';
}
```

## Good example

```ts
@Component({
  selector: 'qs-tree',
  templateUrl: './tree.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TreeComponent {
  readonly selected = signal('');
}
```

## Change history

- 2026-08-12: Initial default-on rule grounded in Quality Studio's OnPush explorer implementation.
