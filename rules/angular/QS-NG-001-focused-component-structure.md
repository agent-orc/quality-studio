---
id: QS-NG-001
version: 1.0.0
title: Keep Angular components focused and colocated
language: angular
kinds: [code]
appliesTo: [**/*.ts, **/*.html, **/*.css, **/*.scss]
severity: medium
defaultOn: false
autofixable: false
deterministic: false
---

## Statement

Keep each standalone feature component focused on one user-facing responsibility; give it a folder that colocates TypeScript, external template, styles, and tests; expose cross-feature symbols through the feature barrel; and move reusable state or transport behavior into an injected service.

## Rationale

Focused components make review scope, signal flow, and ownership visible. Agent Studio's `app/features/<name>/{models,state,components,services}` layout and Quality Studio's explorer, editor, and review-panel folders demonstrate the intended boundary. A page-sized component that owns unrelated API, navigation, and presentation logic is harder to test and reuse.

## Bad example

```ts
@Component({ template: `<main>Everything</main>` })
export class WorkspaceComponent {
  // Repository selection, editor virtualization, review submission, and dialogs.
}
```

## Good example

```ts
@Component({
  standalone: true,
  templateUrl: './explorer.html',
  styleUrl: './explorer.css',
})
export class ExplorerComponent {
  readonly api = inject(QualityApi);
  readonly selectedPath = input.required<string>();
}
```

## Change history

- 2026-08-12: Initial Angular seed grounded in Agent Studio and Quality Studio feature-folder layouts.
