---
id: QS-NG-001
title: Keep Angular components focused and colocated
language: angular
severity: medium
autofixable: false
default-enabled: true
version: 1.0.0
status: active
kinds: [code]
levels: [file, module, project]
applies-to: [.ts]
references: [frontend/README.md, frontend/src/app/review-panel/review-panel.ts, Agent Studio frontend/AGENTS.md]
deterministic-check: none
---

## Statement

Keep one focused standalone component in its own feature folder, with its controller, external template, stylesheet, and behavior tests colocated. Expose cross-feature dependencies through an intentional public surface rather than deep imports.

## Rationale

Quality Studio colocates each workbench surface under `frontend/src/app`, while Agent Studio formalizes folder-per-component and feature-barrel boundaries. Focused components make ownership, review scope, lazy movement, and test responsibility visible.

## Bad example

```ts
// feature.ts owns unrelated navigation, editing, reporting, and dialog behavior.
@Component({ templateUrl: './feature.html' })
export class FeatureComponent { /* several independent surfaces */ }
```

## Good example

```ts
@Component({
  selector: 'qs-review-panel',
  templateUrl: './review-panel.html',
  styleUrl: './review-panel.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPanel { /* review-panel behavior only */ }
```

## Change history

- 2026-08-12, v1.0.0: Initial Angular component-structure rule grounded in the Agent Studio and Quality Studio feature layouts.
