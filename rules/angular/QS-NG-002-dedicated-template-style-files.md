---
id: QS-NG-002
title: Keep non-trivial templates and styles in their own files
technology: Angular
category: component-structure
kinds: [code]
severity: low
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

Use `templateUrl`/`styleUrl` pointing at sibling `.html`/`.css` files for any
component whose markup or styling is more than a couple of lines; reserve
inline `template`/`styles` strings for genuinely trivial components.

## Rationale

Inline template/style strings lose syntax highlighting, formatting, and
template-language tooling (Angular language service diagnostics) in most
editors, and turn a component's `.ts` file into an unreviewable wall of
escaped markup once it grows past a few lines. Every feature component in
this codebase already follows the file-per-concern split; a new component
that inlines a large template is the one that stands out for the wrong
reason during review.

## Good example

```ts
// review-actions.ts
@Component({
  selector: 'qs-review-actions',
  imports: [FormsModule],
  templateUrl: './review-actions.html',
  styleUrl: './review-actions.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewActions { /* ... */ }
```

## Bad example

```ts
@Component({
  selector: 'qs-review-actions',
  template: `
    <div class="active-run-meta">...
    <!-- forty more lines of inline markup, unformatted, unhighlighted -->
    </div>
  `,
  styles: [`.active-run-meta { display: flex; /* ... dozens more rules ... */ }`],
})
export class ReviewActions { /* ... */ }
```
