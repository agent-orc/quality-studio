---
id: QS-NG-005
title: Extract a repeated UI fragment into a shared component, not copy-pasted markup
technology: Angular
category: component-reuse
kinds: [code]
severity: high
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

When the same UI fragment (markup plus its behavior) appears in two or more
feature components, extract it into a shared standalone component rather
than duplicating the template and CSS in each place it's needed.

## Rationale

Duplicated markup drifts: a fix or a11y improvement applied to one copy is
easy to forget in the others. This codebase currently has no `shared/`
component directory at all (`frontend/src/app/` only contains feature
directories — `attack-coverage/`, `editor/`, `explorer/`,
`project-dashboard/`, `review-actions/`, `review-panel/`, `usage-history/`),
so every reusable fragment introduced so far has been re-expressed as
feature-local CSS instead of a shared component. The severity badge is a
concrete example: `project-dashboard.css` (`.severity`) and the coverage
verdict styling in `attack-coverage.css` (`.verdict-pass`, `.verdict-finding`,
`.cell-flag`) both independently re-implement "a small colored status pill,"
each with its own markup and its own color mapping, instead of one
`qs-severity-badge` component both features render.

## Good example

```ts
// a new shared/severity-badge/severity-badge.ts, referenced by any feature
@Component({
  selector: 'qs-severity-badge',
  templateUrl: './severity-badge.html',
  styleUrl: './severity-badge.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeverityBadge {
  readonly severity = input.required<'critical' | 'high' | 'medium' | 'low' | 'info'>();
}
```

## Bad example

```css
/* project-dashboard.css and attack-coverage.css each define their own
   independent "colored status pill" instead of sharing one component */
.severity { padding: 2px 4px; border-radius: 3px; text-transform: uppercase; }
.verdict-pass { background: color-mix(in srgb, var(--status-fresh) 18%, transparent); }
```
