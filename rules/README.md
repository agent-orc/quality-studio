# Quality Studio rule library

Named, versioned coding-standard rules — the QS-90 direction named in
[`docs/operations/quality-concept/index.html`](../docs/operations/quality-concept/index.html#rules)
as the first gap to close in the (until now, empty) rule library.

This tree is the **authored source of truth**. Each rule is one Markdown file so it has its own
git history and can be reviewed like any other change. A generated JSON catalogue
(`src/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json`, validated by
[`schemas/rule-catalogue.v1.schema.json`](../schemas/rule-catalogue.v1.schema.json)) is what the
Quality Studio API actually loads at runtime — see [Review integration](#review-integration).

## Directory layout

```
rules/
  README.md                 this file
  CHANGELOG.md               library-level version history
  angular/QS-NG-###-slug.md  Angular / TypeScript seed set
  dotnet/QS-CS-###-slug.md   C#/.NET seed set
```

## Rule id scheme

Stable, human-readable ids of the form `QS-<TECH>-<NNN>`: `QS-NG-###` for Angular/TypeScript,
`QS-CS-###` for C#/.NET. Ids never get reused or renumbered — a retired rule is marked
`enabled: false` in its frontmatter and kept in the tree with its full change history, not
deleted, so historical findings that cite it remain explainable.

## Rule file format

Each rule is one Markdown file with YAML-ish frontmatter followed by four required sections.

```markdown
---
id: QS-NG-001                 # stable id, never reused
version: 1.0.0                # this rule's own semver (see Versioning)
title: Use design tokens, not raw values
technology: angular            # angular | dotnet
category: design-tokens         # free-form grouping, e.g. component-structure, api-shape
severity: medium                # critical | high | medium | low | info
defaultOn: true                 # part of the DEFAULT-ON core (see Defaults and overrides)
autofixable: false               # true only if a deterministic tool can safely fix it unattended
deterministicRuleIds: []         # cross-references into sensor rule ids this rule's autofix/precheck maps to (e.g. NG8102, CS8618)
relatedGuideline: angular-typescript   # optional: the existing coarse GuidelineStore catalogue bucket this overlaps with
since: 1.0.0                    # library version this rule was introduced in
---

## Statement
One clear, imperative sentence or two: what to do.

## Rationale
Why it matters here — the concrete cost of not doing it.

## Good example
A real snippet from this repo (or Agent Studio) that already follows the rule.

## Bad example
A plausible violation, for contrast.

## Change history
- 1.0.0 (2026-08-27): Initial rule.
```

`severity` and `autofixable` are read at runtime; `goodExample`/`badExample` are extracted from
the fenced code blocks under those two headings. See
[`scripts/sync-rule-catalogue.mjs`](../scripts/sync-rule-catalogue.mjs) for the exact parser.

## Defaults and overrides

The rules marked `defaultOn: true` are the **default-on core**: they apply automatically to
every reviewed project, with no per-repository install step. A project overrides (disables or
adjusts) individual rules with an in-repo JSON file — see
[`schemas/rule-config.v1.schema.json`](../schemas/rule-config.v1.schema.json) for the full
schema and [`docs/operations/rule-library/index.html`](../docs/operations/rule-library/index.html)
for the resolution semantics (built-in → optional shared "global" file → project file, project
wins).

Project override file: **`.quality/rules/overrides.json`** (repository-relative, committed
alongside the code it governs — there is no central per-project settings store). Example,
disabling one rule and softening another's severity:

```json
{
  "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
  "schemaVersion": 1,
  "overrides": [
    { "id": "QS-NG-003", "enabled": false, "reason": "This repo's admin shell is a single generated component; the one-feature-one-folder split does not apply." },
    { "id": "QS-CS-004", "severity": "low", "reason": "Snapshot-fixture tests here are intentionally table-driven and share a fixture by design." }
  ]
}
```

## Review integration

`RuleCatalogueResolver` (`src/AgentOrchestrator.CodeQuality/RuleLibrary.cs`) resolves the
effective rule set (built-in + overrides) and renders each enabled rule as a synthetic "global"
review input with its own `## QS-NG-001`-style heading, carried into review prompts by the
existing `InputResolver`/prompt-budget machinery unchanged. The model is already instructed
(`prompts/file-code-review.v1.md`) to set a finding's `ruleId` to the exact heading id that
caused it, so named-rule findings are traceable end to end via
`GET /api/repos/{id}/rules` (`traces`).

`deterministicRuleIds` on a rule cross-references sensor-native rule ids (SARIF/Roslyn/ESLint
rule codes) that the static-analysis wave (see `docs/operations/static-analysis/`) already
reports deterministically — see the dossier for which of today's rules are enforceable that way
versus agent-review-only.

## Versioning

- Each rule carries its own `version` and a `changeHistory` list — edit both together whenever a
  rule's statement or severity changes materially.
- `catalogueVersion` in the generated JSON is the library-level version, bumped whenever a rule is
  added, retired (`enabled: false`), or has a breaking semantic change.
- Regenerate the JSON catalogue after editing any rule file:

  ```sh
  node scripts/sync-rule-catalogue.mjs
  ```
