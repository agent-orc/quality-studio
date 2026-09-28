---
id: QS-NG-014
version: 1.0.0
title: Keep the TypeScript program type-clean under strict settings
technology: angular
kinds: [code]
category: static-analysis
severity: medium
defaultOn: false
autofixable: false
deterministicRuleIds: [TS*]
relatedGuideline: angular-typescript
since: 1.6.0
---

## Statement

Keep every TypeScript project of the workspace free of `tsc` diagnostics under its strict
compiler settings. Fix a type error at its cause rather than widening a type to `any`, adding a
non-null assertion that the code cannot justify, or silencing the line with `@ts-ignore`. When a
suppression is unavoidable, use `@ts-expect-error` with a reason so it fails once the error is gone.

## Rationale

Strict type checking catches null dereferences, missing cases and mismatched contracts before
they reach a browser. The value depends on the program staying clean: once errors are tolerated,
new ones hide among the old. Quality Studio's tsc sensor checks each project a solution-style
`tsconfig.json` references, so a workspace cannot look clean just because its root config
compiles no files.

## Detection

Use the tsc sensor's findings for the reviewed files: each carries its `TS` diagnostic code and
location. Explain the type contract that is violated and the proportionate fix. Flag new `any`,
unjustified `!` assertions and `@ts-ignore` comments that exist to avoid a diagnostic. A
diagnostic in generated or vendored code is not a finding against the reviewed source.

## Good example

```jsonc
{
  "compilerOptions": {
    "strict": true,
    "noImplicitOverride": true,
    "noPropertyAccessFromIndexSignature": true,
    "noImplicitReturns": true
  },
  "files": [],
  "references": [{ "path": "./tsconfig.app.json" }]
}
```

## Bad example

```ts
// @ts-ignore -- the response type is wrong, fix later
const repository: Repository = response.items[0] as any;
```

## Change history

- 1.0.0 (2026-09-28): Initial opt-in rule that owns the TypeScript diagnostic family reported by the tsc sensor.
