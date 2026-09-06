---
id: QS-NG-005
version: 1.1.0
title: Default to OnPush with signal-driven state
technology: angular
kinds: [code]
category: change-detection
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: angular-typescript
since: 1.0.0
---

## Statement

Every component sets `changeDetection: ChangeDetectionStrategy.OnPush` and drives its template
from `signal`/`computed`/`input`/`output`, not from mutated plain fields or manual
subscriptions that write to component properties outside Angular's change-detection triggers.

## Rationale

Every feature component in `frontend/src/app` already declares `OnPush` and reads state through
signals; this is what keeps `explorer`, `review-panel`, and `usage-history` fast on large trees
and result sets. A component that drops to `Default` (or mutates a field from inside a manual
`subscribe()`) silently re-introduces full subtree re-checks and can even fail to render at all
under `OnPush` siblings if it relies on ambient change detection to pick up its mutations.

## Detection

Check the `@Component` decorator for `changeDetection: ChangeDetectionStrategy.OnPush`. Then look for state written outside signals: plain mutable fields assigned from `subscribe()` callbacks, `setTimeout`/event handlers, or `ChangeDetectorRef.detectChanges()` calls that exist only to make such mutations visible.

## Good example

```typescript
// frontend/src/app/review-panel/review-panel.ts
@Component({
  selector: 'app-review-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // ...
})
export class ReviewPanel {
  readonly severityFilter = signal<string>('all');
  readonly visibleFindings = computed(() => /* derive from signals */ []);
}
```

## Bad example

```typescript
@Component({ selector: 'app-widget' /* no changeDetection: OnPush */ })
export class Widget implements OnInit {
  findings: Finding[] = [];
  ngOnInit() {
    this.api.findings$.subscribe(value => { this.findings = value; }); // mutates a plain field
  }
}
```

## Change history

- 1.1.0 (2026-09-06): Declared the applicable review kinds and added detection guidance for the generated catalogue.
- 1.0.0 (2026-08-27): Initial rule, grounded in the `ChangeDetectionStrategy.OnPush` declaration
  already present on every component under `frontend/src/app` (`review-panel.ts`, `explorer.ts`,
  `usage-history.ts`, `app.ts`, `attack-coverage.ts`).