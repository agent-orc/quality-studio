---
id: QS-NG-002
title: Use central design tokens instead of ad-hoc style values
language: angular
severity: medium
autofixable: false
version: 1.0.0
status: active
kinds: [code]
levels: [file, module, project]
applies-to: [.css, .scss, .html]
references: [frontend/src/styles.css, frontend/DESIGN-KINSHIP.md, Agent Studio frontend/src/styles/_tokens-semantic.scss]
deterministic-check: quality-rules/design-token-literals
---

## Statement

Outside the central token definitions, use central design tokens for shared spacing, color, typography, radius, control-size, and state values. Do not introduce local hard-coded pixels, colors, or badge geometry when an existing semantic or scale token covers the value.

## Rationale

Both Studio codebases define a compact `--studio-*` vocabulary and a shared spacing scale. Ad-hoc values break theme parity and let neighboring controls drift. A new token belongs in the central scale only when no existing semantic value expresses the design decision.

## Bad example

```css
.finding-card {
  margin: 12px;
  padding: 8px 16px;
  color: #66707e;
  border-radius: 6px;
}
```

## Good example

```css
.finding-card {
  margin: var(--studio-space-3);
  padding: var(--studio-space-2) var(--studio-space-4);
  color: var(--studio-fg-muted);
  border-radius: var(--studio-radius-card);
}
```

## Change history

- 2026-08-12, v1.0.0: Initial rule covering the operator-observed ad-hoc-style defect class.
