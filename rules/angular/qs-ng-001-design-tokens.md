---
id: QS-NG-001
title: Style with design tokens, not raw literals
summary: Component styles must consume the shared design-token custom properties instead of hard-coded hex colors, px spacing, or radii.
technology: angular
category: design-tokens
severity: high
autofixable: false
defaultOn: true
kinds: [code]
levels: [file]
version: 1.0.0
status: active
deterministicCheck: {tool: stylelint, ruleId: scale-unlimited/declaration-strict-value}
---
## Statement

Component stylesheets (`.css`/`.scss`) MUST reference the shared design-token
custom properties (`--studio-*`) for color, spacing, radius, and typography
values. A raw hex color, an unscoped `px` literal for spacing, or a duplicated
magic number is a finding, not a style preference.

## Rationale

Two components that express "the same" concept — e.g. finding severity — with
independently chosen literals silently diverge the moment either one is
edited, and neither responds to a theme change (dark mode, brand refresh).
Tokens are the single place that concept is allowed to be defined; consuming
them via `var(...)` is what keeps every surface visually consistent and
themeable without a repo-wide find-and-replace. This is the exact defect
class observed in review: ad-hoc literals instead of design tokens.

## Good example

`frontend/src/app/review-panel/review-panel.css:13` derives every severity
color from the shared token set:

```css
.severity.critical { color: var(--studio-severity-critical); }
.severity.high     { color: var(--studio-severity-high); }
.severity.medium   { color: var(--studio-severity-medium); }
.severity.low      { color: var(--studio-severity-low); }
.severity.info     { color: var(--studio-severity-info); }
```

## Bad example

`frontend/src/app/project-dashboard/project-dashboard.css:11` re-expresses the
identical severity concept with independently chosen hex literals, so the
dashboard's severity colors already disagree with the review panel above and
will not respond to `[data-theme="dark"]`:

```css
.severity.critical, .severity.high { color: #ef7777; }
.severity.medium                   { color: #dca85c; }
.severity.low, .severity.info      { color: #71aeda; }
```

`frontend/src/app/app.css:5` and `:33-34` show the same drift for brand and
error colors, mixed into otherwise token-aware declarations:

```css
.brand-mark { color: #fff; background: linear-gradient(145deg, #3c7ba6, #244a68); }
.form-error { border: 1px solid color-mix(in srgb, #e05252 55%, var(--studio-border)); }
.primary-button, .secondary-button, .danger-button { height: 32px; padding: 0 12px; }
```

## Notes

Agent Studio enforces this deterministically with the `stylelint-declaration-strict-value`
plugin (`frontend/.stylelintrc.json`), with an explicit, shrinking "legacy,
migration pending" exemption list — see
`project-hygiene-badge.component.scss:15,19` (`color: #f9e2af`, `color: #fab387`)
for a real, currently-exempted violation. Quality Studio's own frontend has no
stylelint configuration yet, so this rule is the only enforcement point until
one is added. Marked `autofixable: false`: the plugin flags a bare literal
deterministically, but choosing the *correct* semantic token is a judgment
call the tool cannot make automatically.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in the Quality Studio and Agent
  Studio frontends during the QS-90 rule-library seeding session.
