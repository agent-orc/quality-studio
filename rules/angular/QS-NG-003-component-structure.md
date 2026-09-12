---
id: QS-NG-003
version: 1.2.0
title: Focus components and respect declared feature boundaries
technology: angular
kinds: [code]
category: component-structure
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: [quality-architecture/layer-imports]
relatedGuideline: angular-typescript
since: 1.0.0
---

## Statement

Group Angular code by declared ownership: infrastructure and contracts in core, reusable
presentation and pure utilities in shared, product behavior in features, and application
composition in shell. Follow the repository's own architecture contract when it declares a
different layout. Keep components focused and colocate their implementation, template,
styles and tests. Extract smaller components through explicit inputs/outputs when a feature
becomes complex; one feature may contain several collaborating components.

## Rationale

Flat component folders and broad app components obscure ownership. Feature services leaking
into shared presentation or lower layers importing feature components produce dependency
cycles and make otherwise reusable controls depend on the entire application. Component
composition should make ownership clearer instead of forcing one large component per feature.

## Detection

Compare ownership and import direction against the declared architecture. Quality Studio's
`frontend/lint/architecture.config.mjs` permits core to use core and pure shared utilities;
shared uses shared and core models; features use core, shared and their own feature;
shell composes features. Flag reverse dependencies, undeclared cross-feature imports and
unrelated concerns accumulated in a component. Do not flag a documented alternative layout
or a focused child component simply because a feature contains multiple components.

## Good example

```text
frontend/src/app/
  core/api/quality-api.ts
  shared/ui/empty-state/empty-state.ts
  features/code/editor/editor.ts
  features/code/container-view/container-view.ts
  shell/workbench/workbench.ts
```

## Bad example

```ts
// shared/ui/status.ts: reusable presentation now owns a product feature dependency.
import { Editor } from '../../features/code/editor/editor';
```

## Change history

- 1.2.0 (2026-09-12): Added declared layer boundaries, deterministic ESLint mapping and explicit permission for focused child component composition.
- 1.1.0 (2026-09-06): Declared the applicable review kinds and added detection guidance for the generated catalogue.
- 1.0.0 (2026-08-27): Initial feature-folder rule.
