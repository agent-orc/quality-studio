---
id: QS-NG-007
title: Use the built-in @if/@for control flow, not *ngIf/*ngFor
technology: Angular
category: template-hygiene
kinds: [code]
severity: low
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

Write conditional and repeated template content with the built-in `@if`,
`@else if`, `@else`, and `@for`/`track` blocks. Do not introduce the
`*ngIf`/`*ngFor` structural directives.

## Rationale

The built-in control flow does not require importing `CommonModule` or the
individual directives, catches a missing `track` expression at compile time
for `@for`, and is measurably faster to parse for the Angular compiler than
structural directives. Every template in this codebase already uses `@if`/
`@for` exclusively (`review-actions.html`, `attack-coverage.html`,
`project-dashboard.html`, and the rest) — there is not a single `*ngIf` or
`*ngFor` left in the frontend — so this rule mainly guards against
regression, e.g. a contributor pasting an example from older Angular
documentation.

## Good example

```html
<!-- review-actions.html -->
@if (run.state === 'queued' || run.state === 'running') {
  <button type="button" (click)="api.pauseReview(run.id)">Pause</button>
}
@for (kind of reviewKinds; track kind) {
  <option [value]="kind">{{ kind }}</option>
}
```

## Bad example

```html
<button type="button" *ngIf="run.state === 'queued' || run.state === 'running'"
  (click)="api.pauseReview(run.id)">Pause</button>
<option *ngFor="let kind of reviewKinds" [value]="kind">{{ kind }}</option>
```
