---
id: QS-NG-005
title: Make change detection explicit and bounded
language: angular
severity: medium
autofixable: false
version: 1.0.0
status: active
kinds: [code, performance]
levels: [file, module, project]
applies-to: [.ts]
references: [frontend/src/app/review-panel/review-panel.ts, frontend/src/app/app.config.ts, Agent Studio frontend/AGENTS.md]
deterministic-check: none
---

## Statement

Use signals, `computed`, and `OnPush` components to bound UI updates. Prefer event coalescing or zoneless primitives at the application boundary, avoid mutable state hidden from Angular, and use `effect` only for real side effects with an explicit lifetime.

## Rationale

Quality Studio already combines signal-based view state, `OnPush`, and event coalescing. Agent Studio uses the same declarative state direction. Explicit dependencies prevent whole-tree checks, stale views, subscription leaks, and effects that accidentally become a second state system.

## Bad example

```ts
items: Item[] = [];
ngOnInit() { this.api.items$.subscribe(items => this.items = items); }
```

## Good example

```ts
readonly items = toSignal(this.api.items$, { initialValue: [] });
readonly visibleItems = computed(() => this.items().filter(item => item.visible));
```

## Change history

- 2026-08-12, v1.0.0: Initial Angular change-detection rule.

