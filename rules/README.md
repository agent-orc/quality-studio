# Quality Studio rule library

Named, versioned coding-standard rules — the QS-90 direction named in
[`docs/operations/quality-concept/index.html`](../docs/operations/quality-concept/index.html#rules)
as the first gap to close in what was an empty rule library.

This tree is the **authored source of truth**. Each rule is one Markdown file so it has its own
git history and can be reviewed like any other change.

## What runs where

| Step | Artefact |
| --- | --- |
| Authoring | `rules/<technology>/<id>-<slug>.md`, this tree |
| Generation | [`scripts/sync-rule-catalogue.mjs`](../scripts/sync-rule-catalogue.mjs) (`npm run rules:sync`) |
| Contract | [`schemas/rule-catalogue.v1.schema.json`](../schemas/rule-catalogue.v1.schema.json) |
| Generated catalogue | `backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json`, committed |
| Load | embedded resource in the analysis-core assembly, read once per process by `RuleCatalogueResolver` |
| Injection | `InputResolver` renders the effective rules as built-in review inputs |
| Inspection | `GET /api/rules`, `GET /api/repos/{repoId}/rules` |

The generated catalogue is committed and embedded, not fetched: a review has no network step and
no version skew between the analysis core and the rules it enforces. Regenerate it in the same
commit as the rule change — `npm run rules:check` fails the build otherwise.

The generator is deterministic. Entries are sorted by id, object keys have a fixed order, and no
timestamp or commit hash is written, so `--check` only ever reports a real difference between the
rule tree and the catalogue.

## Directory layout

```
rules/
  README.md                  this file
  CHANGELOG.md               library-level version history
  angular/QS-NG-###-slug.md  Angular / TypeScript rules
  dotnet/QS-CS-###-slug.md   C#/.NET rules
  generic/QS-GN-###-slug.md  language-independent rules
```

## Rule id scheme

Stable, human-readable ids of the form `QS-<TECH>-<NNN>`: `QS-NG-###` for Angular/TypeScript,
`QS-CS-###` for C#/.NET, `QS-GN-###` for language-independent rules. The prefix, the directory,
and the `technology` field must agree, and the file name must start with the id.

Ids never get reused or renumbered — a retired rule is marked `enabled: false` in its frontmatter
and kept in the tree with its full change history, not deleted, so historical findings that cite
it remain explainable.

## Rule file format

Each rule is one Markdown file with YAML-ish frontmatter followed by six required sections.

```markdown
---
id: QS-NG-001                 # stable id, never reused
version: 1.1.0                # this rule's own semver (see Versioning)
title: Use design tokens, not raw values
technology: angular            # angular | dotnet | generic
kinds: [code]                  # code | security | performance; at least one
category: design-tokens         # free-form grouping, e.g. component-structure, api-shape
severity: medium                # critical | high | medium | low | info
defaultOn: true                 # part of the DEFAULT-ON core (see Defaults and overrides)
autofixable: false               # true only if a deterministic tool can safely fix it unattended
deterministicRuleIds: []         # cross-references into sensor rule ids this rule's autofix/precheck maps to (e.g. NG8102, CS8618)
relatedGuideline: angular-typescript   # optional: the existing coarse GuidelineStore catalogue bucket this overlaps with
since: 1.0.0                    # library version this rule was introduced in
enabled: true                   # optional, defaults to true; false retires the rule without deleting it
---

## Statement
One clear, imperative sentence or two: what to do.

## Rationale
Why it matters here — the concrete cost of not doing it.

## Detection
What a reviewer looks at to decide, and what does not count as a violation.

## Good example
A real snippet from this repo (or Agent Studio) that already follows the rule.

## Bad example
A plausible violation, for contrast.

## Change history
- 1.1.0 (2026-09-06): Newest entry first.
- 1.0.0 (2026-08-27): Initial rule.
```

All six sections are required. `technology` decides which repository units a rule reaches
(`angular`, `dotnet`, `generic`; `generic` applies everywhere), and `kinds` decides which review
kinds it is injected into. `severity` and `autofixable` are read at runtime; `goodExample` and
`badExample` are the fenced code block under those two headings; `statement`, `rationale`, and
`detection` are the prose with whitespace collapsed. `## Change history` is newest first, and its
top entry's version must equal the frontmatter `version`. See
[`scripts/sync-rule-catalogue.mjs`](../scripts/sync-rule-catalogue.mjs) for the exact parser.

## Defaults and overrides

The rules marked `defaultOn: true` are the **default-on core**: they apply automatically to
every reviewed project, with no per-repository install step. A project overrides (disables or
adjusts) individual rules with an in-repo JSON file — see
[`schemas/rule-config.v1.schema.json`](../schemas/rule-config.v1.schema.json) for the full schema
and [`docs/review-inputs.md`](../docs/review-inputs.md#rule-library) for the resolution semantics
(built-in → optional shared "global" file → project file, project wins).

An override names a known rule id, sets `enabled`, `severity`, or both, and states a reason. An
unknown id, a missing reason, and an override that changes nothing are all errors: a rule the
repository believes it disabled must never be silently active. The global file is
`rule-overrides.json` in the configured global inputs directory.

Project override file: **`.quality/rules/overrides.json`** (repository-relative, committed
alongside the code it governs — there is no central per-project settings store). Example,
disabling one rule and softening another's severity:

```json
{
  "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
  "schemaVersion": 1,
  "overrides": [
    { "id": "QS-NG-003", "enabled": false, "reason": "This repo's admin shell is a single generated component; the one-feature-one-folder split does not apply." },
    { "id": "QS-CS-004", "severity": "low", "reason": "Snapshot-fixture tests here are intentionally table-driven and share a fixture by design." }
  ]
}
```

## Review integration

`RuleCatalogueResolver` (`backend/AgentOrchestrator.CodeQuality/RuleLibrary.cs`) resolves the
effective rule set (built-in + overrides). `InputResolver` renders each enabled, applicable rule
as a built-in-scope review input with its own `## QS-NG-001`-style heading, and the existing
prompt-budget machinery carries it into the review prompt unchanged — no prompt template knows
about rules.

Two filters decide which rules a review sees. `kinds` selects the review kind, and `technology`
must match the reviewed unit's hierarchy adapter (`dotnet`, `angular`, `generic`), where `generic`
rules match every adapter. That filtering is what keeps the rules inside the character budget: a
review spends roughly 4,000 to 5,500 of its 12,000 characters on them.

The prompt receives a rule's title, effective severity, statement, and detection guidance. Its
rationale and its two worked examples stay in the catalogue and on the rules endpoint, where a
reader wants them and no budget is at stake.

The model is already instructed (`prompts/file-<kind>-review.v1.md`) to set a finding's `ruleId`
to the exact heading id that caused it. `ReviewResponseParser` then accepts only ids the resolved
inputs actually carry, case-insensitively, and replaces anything else with `built-in:<kind>`;
see [`docs/finding-lifecycle.md`](../docs/finding-lifecycle.md) for why that matters to finding
identity. Named-rule findings are traceable end to end via `GET /api/repos/{id}/rules`
(`traces`).

`deterministicRuleIds` on a rule cross-references sensor-native rule ids (SARIF/Roslyn/ESLint
rule codes) that the static-analysis wave (see `docs/operations/static-analysis/`) already
reports deterministically — see the dossier for which of today's rules are enforceable that way
versus agent-review-only.

## Versioning

- Each rule carries its own `version` and a `## Change history` list, newest first — edit both
  together whenever a rule's statement, detection guidance, kinds, or severity changes materially.
  A rule's version is what `reviewInputs.standards[]` records in every sidecar it informed.
- `catalogueVersion` in the generated JSON is the library-level version. It is read from the top
  `## <version>` heading of [`CHANGELOG.md`](CHANGELOG.md), so the changelog is the single place a
  library version is declared. Bump it whenever a rule is added, retired (`enabled: false`), or
  changes semantically.
- Regenerate the JSON catalogue in the same commit as the rule change:

  ```sh
  npm run rules:sync     # writes backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json
  npm run rules:check    # exits 1 if the catalogue and the rule tree disagree; runs in CI
  ```

A version-only bump does not invalidate stored reviews: the effective input hash covers the rule
text that reached the prompt, not its version number. Change the text, and the affected units
report `policyDrift`.

## Review criteria in the tool and website

The application header opens **Review policy**. **Criteria & metrics** shows the effective
named-rule library returned by the repository API, including enablement, severity,
override reasons, rationale, detection guidance and examples. **Repository guidelines**
remains the editor for repository-owned input files.

The **Prompt input preview** shows the repository-wide, file-level input resolution for
the selected review kind, across all technologies. It displays the actual included text
and explicit character-budget or override omissions. It is not a stored prompt from a
completed run: an individual review also filters by its unit's adapter and level.

`review-methodology.json` owns the cross-cutting explanations and exact implemented metric
formulas, their interpretation, limits and source references. It references existing named
rules instead of duplicating them. `scripts/sync-review-reference.mjs` derives the Angular
reference data and the website's criteria section from this file and the generated named-rule
catalogue. The website describes shipped defaults; the application shows repository overrides.

Run `npm run rules:sync` after changing a rule or the methodology. `npm run rules:check`
checks both generated surfaces for drift. `npm run test:review-reference` checks rule/source
references, source-file links and safe HTML rendering. Formulas link to the implementing code;
update the formula explanation when that calculation changes. Coverage, grading and ranking
heuristics retain their distinct meanings and do not establish that a program is correct.
