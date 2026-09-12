# Frontend structure, typography, and shared styles

Status: implemented, 12 September 2026.

## Repository layout

The repository has two explicit implementation roots. Backend sources and C# tests
live together under `backend`; the Angular application remains self-contained under
`frontend`. The root solution, launch commands, documentation, and repository-wide
Node tooling remain at the repository root.

```text
backend/
  AgentOrchestrator.CodeQuality/  # analysis core
  QualityStudio.Api/             # HTTP host
  quality-cli/                   # command-line host
  tests/                # core/API tests and shared C# test helpers
frontend/
  style-reference/      # separate development reference application
  src/app/
    app.config.ts       # application providers
    shell/              # composition, navigation, pane layout
    core/               # API transport, contracts, navigation utilities
    features/           # code, dashboard, reviews, repositories, settings, security
    shared/             # dialogs, UI, styles, pure utilities
docs/
rules/
scripts/
tests/                  # repository-wide Node tooling tests and coverage baselines
QualityStudio.slnx
```

This keeps backend test ownership clear and avoids a generic root `src` that really
means only backend. It also preserves familiar Angular tooling without introducing
an additional workspace framework. Solution references, CI, Docker, launch scripts,
performance fixtures, and maintained documentation use the new paths.

## Angular ownership

The shell composes the features. Repository and guideline editing have dedicated
state classes scoped to the application instance; pane persistence and resizing
live in `WorkspaceLayoutState`. Shared confirmation behavior and the API connection
state have explicit owners. The shell retains navigation orchestration.

Import boundaries are checked structurally: features depend on their own feature,
core, and shared; shared has only the narrow contracts dependency on core/models;
core cannot depend on features or shell. Dynamic imports and re-exports are checked
as well. No component framework or empty wrapper directives were introduced.

## Shared visual contract

| Role | Size |
| --- | --- |
| Body and code | 13px |
| Controls, table text, tabs, navigation | 12px |
| Metadata and supporting labels | 11px |
| Pane titles | 14px |
| Section headings | 16px |

`shared/styles/tokens.css` owns both themes and semantic typography. Native controls
use shared classes from `primitives.css`: buttons, icon buttons, fields, table
headings, row buttons, badges, and notices, including hover, keyboard focus,
disabled, and validation states. `syntax.css` owns shared code-token colors.
`src/styles.css` is the explicit entry point for those three global layers.

View-specific layout stays with its component. Review-panel implementation styles
are component-owned partials, and obsolete editor selectors for markup now rendered
by ContainerView were removed. Component style and bundle budgets stay enforced.

Virtualized layout uses one `WORKSPACE_METRICS` contract for code lines (24px),
explorer rows (32px), and container rows (44px), bound to CSS custom properties on
the component host. Table headings and rows share their column sizing and scrolling
container. Pane widths adapt to the viewport without overwriting saved preferences.

## Connection and navigation behavior

An unavailable API produces a blocking connection message with retry and API-access
actions. The app no longer silently substitutes a demonstration repository. Retry
reloads the registry and the selected context. Endpoint errors remain distinct from
transport outages, and stale requests cannot overwrite a newly selected repository.

Dashboard coverage navigation resolves a file even when it has not been loaded into
the lazy explorer tree. Container navigation loads the selected namespace's children,
so a deep link does not leave a blank “Select an item” tab.

## Review enforcement and checks

`quality-architecture.json` declares this repository's layout. The architecture
sensor turns violations into deterministic review evidence; other repositories opt
in by declaring their own contract. ESLint import rules and PostCSS readability rules
also feed the existing SARIF review pipeline. Rule-catalogue guidance covers shared
style ownership and component structure. See [architecture checks](architecture-checks.md).

The separate reference application shows the real shared primitives and states in
both themes, without the product API. From `frontend`:

```sh
npm run reference          # http://127.0.0.1:4226
npm run build:reference
npm run lint
npm run test:architecture  # import boundaries and typography, including SARIF output
npm test
npm run build
```

Regression checks cover outage recovery, stale requests, coverage navigation, pane
interaction, repository form isolation, confirmed guideline deletion, architecture
findings, and historical review IDs across the source moves. Browser checks exercise
light/dark themes, narrow and wide layouts, larger text, table alignment, focus,
virtualized scrolling, and the reference surface. Performance budgets remain in
[PERF.md](../PERF.md); machine-dependent tests retain their existing separate category.

## Recorded frontend acceptance

The integrated source passes ESLint, the 30 architecture/typography rule cases,
the browser-resolver tests, and all 179 Angular tests. Both production builds
pass their budgets. Angular line coverage is 72.08%, above the existing 60.82%
baseline; the baseline was not lowered.

Browser DOM and layout assertions covered both themes at 1600, 1280, 1024, and 853
CSS pixels (the latter two also representing narrower effective viewports when
zoomed). Visible text stayed at least 11px, with no page-wide overflow or JavaScript
errors. Keyboard focus, collapsed panes, dashboard scrolling, and settings/review
dialogs were checked. Namespace navigation displayed 9 file rows; all 10 column
positions matched their headings before and after 554px horizontal scrolling.
Coverage navigation opened a real C# file with 59 rendered code lines. A locally
intercepted API outage showed the blocking state; retry restored the project view.

The existing browser performance checks passed for the 6000-line file fixture and
the 1600-file repository-switch fixture. The separate previously known backend
machine-bound dashboard measurement remains documented in
[backend validation](backend-layout-validation.md). These browser checks do not
claim that the separate backend timing limit is met.
