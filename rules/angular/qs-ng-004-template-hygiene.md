---
id: QS-NG-004
title: Keep templates declarative; move nested logic into the component
summary: Templates should read as markup with simple bindings; deeply nested control-flow blocks and inline multi-step expressions belong in the component class.
technology: angular
category: template-hygiene
severity: medium
autofixable: false
defaultOn: false
kinds: [code]
levels: [file]
version: 1.0.0
status: active
deterministicCheck: {tool: eslint, ruleId: "@angular-eslint/template/conditional-complexity"}
---
## Statement

Use the modern `@if`/`@for` control-flow blocks (never the legacy
`*ngIf`/`*ngFor` structural directives), always supply `track` on `@for`, and
keep each block shallow. When a template nests more than two `@for`/`@if`
levels around inline click handlers and computed expressions, extract a child
component or a computed signal in the class instead of deepening the
template.

## Rationale

A template is read far more often than it is written, usually while tracing
a bug under time pressure. Deeply nested control flow crammed onto one line
turns that trace into re-deriving the component's logic from markup syntax.
Angular's newer `@for`/`@if` syntax with mandatory `track` is also what makes
list re-rendering correct and fast — the compiler cannot help enforce
identity semantics that live only inside an untracked `*ngFor`.

## Good example

Both reviewed repositories consistently use the modern control-flow syntax
with explicit `track` expressions, e.g. `frontend/src/app/app.html:15`:

```html
@for (repository of api.repositories(); track repository.id) {
  ...
}
```

## Bad example

`frontend/src/app/app.html:116` nests an `@if`, two further `@for` loops, and
an inline click handler on a single source line:

```html
@if (guidelineImpact(); as impact) {
  <section class="impact-result" [class.changed]="impact.changed">
    ...
    @for (file of impact.files; track file.path) {
      @for (finding of file.added; track finding.id) { <small>+ {{ finding.severity }} · {{ finding.title }} · {{ file.path }}</small> }
      @for (finding of file.removed; track finding.id) { <small>− {{ finding.severity }} · {{ finding.title }} · {{ file.path }}</small> }
    }
  </section>
}
```

## Recommendation

Give the guideline-impact diff its own child component (e.g.
`<guideline-impact-diff [impact]="guidelineImpact()" />`) so `app.html` binds
one input and the nested iteration lives in a template that only has to
represent that one concern.

## Notes

Agent Studio enforces a template complexity ceiling deterministically with
`'@angular-eslint/template/conditional-complexity': ['error', { maxComplexity: 5 }]`
(`frontend/eslint.config.js:80`). Marked `autofixable: false`: the checker can
flag a template that crossed the complexity threshold, but extracting a child
component is a design decision a tool cannot make for you.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in the Quality Studio frontend
  during the QS-90 rule-library seeding session.
