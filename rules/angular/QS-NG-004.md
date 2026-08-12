---
id: QS-NG-004
title: Keep Angular templates declarative and externally testable
language: angular
severity: medium
autofixable: false
default-enabled: true
version: 1.0.0
status: active
kinds: [code]
levels: [file, module, project]
applies-to: [.ts, .html]
references: [frontend/src/app/review-panel/review-panel.html, frontend/src/app/review-panel/review-panel.ts, Agent Studio frontend/AGENTS.md]
deterministic-check: quality-rules/external-templates
---

## Statement

Use external templates and styles, typed bindings, Angular control flow, stable tracking keys, and accessible native semantics. Move branching, mutation, repeated computation, and formatting out of template expressions into signals, computed values, or small view-model methods.

## Rationale

Declarative templates make the rendered contract readable and keep change-detection work predictable. External files also let Angular template diagnostics, component budgets, focused tests, and reviewers examine presentation independently from controller wiring.

## Bad example

```ts
@Component({
  template: `<div (click)="save()">{{ expensiveLookup(items()) }}</div>`,
})
export class EditorComponent {}
```

## Good example

```ts
@Component({ templateUrl: './editor.html', styleUrl: './editor.css' })
export class EditorComponent {
  readonly visibleItems = computed(() => selectVisible(this.items()));
}
```

```html
<button type="button" (click)="save()">Save</button>
@for (item of visibleItems(); track item.id) { <span>{{ item.name }}</span> }
```

## Change history

- 2026-08-12, v1.0.0: Initial Angular template-hygiene rule.
