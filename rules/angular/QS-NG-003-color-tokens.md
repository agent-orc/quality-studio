---
id: QS-NG-003
title: Color with design tokens, never hard-coded hex or rgb values
technology: Angular
category: design-tokens
kinds: [code]
severity: high
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

Reference an existing `--studio-*` (or `--status-*`) custom property from
`frontend/src/styles.css` for every color, background, and border value in a
component stylesheet. Do not write a literal hex/rgb color, and do not
introduce a new one-off custom property that duplicates a token that already
exists.

## Rationale

`styles.css` already defines a full palette, including semantic severity
colors (`--studio-severity-critical`, `--studio-severity-high`,
`--studio-severity-medium`, `--studio-severity-low`, `--studio-severity-info`)
with light and dark theme values. A hard-coded hex bypasses both the dark
theme override block and any future palette change, and is the exact defect
class this rule exists to stop: `project-dashboard.css` defines a `.severity`
badge that hard-codes `#ef7777`/`#dca85c`/`#71aeda` for critical/high, medium,
and low/info respectively, one line away from the `--studio-severity-*`
tokens that already express the same four severities and already have dark
theme values — the token was available and unused.

## Good example

```css
/* what the .severity rule should read, using the tokens that already exist */
.severity.critical, .severity.high { color: var(--studio-severity-high); }
.severity.medium { color: var(--studio-severity-medium); }
.severity.low, .severity.info { color: var(--studio-severity-info); }
```

## Bad example

```css
/* project-dashboard.css:11 (as shipped) */
.severity.critical, .severity.high { color: #ef7777; }
.severity.medium { color: #dca85c; }
.severity.low, .severity.info { color: #71aeda; }
```
