---
id: QS-NG-011
version: 1.0.0
title: Render long lists and long files through a bounded window
technology: angular
kinds: [performance]
category: virtualization
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

A view over data whose size the repository decides — a file tree, a source file, a findings list, a
run history — renders a fixed window of rows plus a small overscan, sized by the viewport and not
by the data. Index the data once when it arrives instead of scanning it per rendered row.

## Rationale

`explorer.ts` flattens the tree in a `computed()` and slices a window out of it, and the editor
keeps an 80-line overscanned window regardless of file length, which is what holds the under-100 ms
transition on a repository with 3,450 tracked files. The alternative is not slower by a constant:
DOM nodes, bindings, and per-row work all scale with the data, so the first pathological repository
turns a usable pane into an unusable one.

## Detection

For each `@for` over data of repository-determined size, look for a window computed from a scroll
offset and a row height, and check whether per-row expressions search or filter a collection rather
than reading an index built once. A list bounded by a small constant — review kinds, severities,
adapters — is not a violation.

## Good example

```ts
// frontend/src/app/explorer/explorer.ts
readonly treeRows = computed(() => flattenTree(this.api.tree(), this.expanded()));
readonly visibleRows = computed(() =>
  this.filteredRows().slice(start, start + count).map((node, i) => ({ node, top: (start + i) * ROW_HEIGHT })));
```

## Bad example

```html
<!-- Every row of every file, and a scan of all findings per row -->
@for (row of allLines(); track row.number) {
  <span>{{ findings().filter(f => f.line === row.number).length }}</span>
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the windowed tree and editor rendering described
  in `frontend/PERF.md` and implemented in `explorer.ts`.
