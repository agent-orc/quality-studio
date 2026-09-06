---
id: QS-NG-010
version: 1.0.0
title: Derive template collections in computed signals, not in template calls
technology: angular
kinds: [performance]
category: change-detection
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.2.0
---

## Statement

A collection a template iterates or measures is a `computed()` signal. A template expression reads
a signal or a plain field; it does not call a method that filters, maps, sorts, slices, reverses,
or otherwise allocates — and the same derivation is not written twice in one template.

## Rationale

`review-panel.ts` and `review-actions.ts` already expose `scopeRuns`, `comparableRuns`,
`runFindings`, `filteredModels`, and `fileCount` as `computed()`, so each is recomputed when its
inputs change and not once per check. Where that slipped, the cost is visible:
`review-actions.html` calls `runFiles(run, states)` six times per change-detection cycle — twice
per line, once for the length check and once for the loop — and each call returns a fresh array, so
`track file.path` reconciles against objects it has never seen and the list rebuilds.

## Detection

Read every `{{ }}`, `@if`, and `@for` expression: a method call with arguments, `.filter(`,
`.map(`, `.slice(`, `.sort(`, `.reverse(`, an array or object literal, or the same call appearing
twice in one template is a violation. Reading a signal, a `computed()`, or a plain field is not,
and a pure formatting call on a scalar is not either.

## Good example

```ts
// frontend/src/app/review-panel/review-panel.ts
readonly scopeRuns = computed(() => this.api.runs().filter(run => run.scope === this.scope()));
readonly runFindings = computed(() => this.indexFindings(this.selectedRun()));
```

## Bad example

```html
<!-- runFiles() allocates a new array on every call, and this calls it twice per line -->
@if (runFiles(run, states).length) {
  @for (file of runFiles(run, states); track file.path) { <li>{{ file.path }}</li> }
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the `computed()` derivations in `review-panel.ts`
  and the repeated `runFiles(...)` template calls in `review-actions.html`.
