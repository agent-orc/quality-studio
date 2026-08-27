# Quality Studio rule library

This directory is Quality Studio's own library of named, language-specific best
practices — the "this is good code" rules the operator asked for on 2026-08-12.
It is separate from `.quality/inputs/*.md` (per-repository freeform guidelines
managed by `GuidelineStore`, see `docs/operations/quality-concept/index.html`
for why that catalogue alone was judged too generic). Rules here are granular,
individually versioned, and named so findings can cite exactly which rule fired.

## Why a second catalogue instead of extending `GuidelineStore`

`GuidelineStore.Catalogue` already ships four broad guidelines
(`dotnet-api-safety`, `angular-typescript`, `testing-confidence`,
`security-boundaries`) and its id validation
(`^[a-z0-9][a-z0-9._-]{1,127}$` in `GuidelineStore.cs`) is lowercase-only and
was written for a handful of hand-authored, per-repo-installable blobs, not for
a growing library with severity, autofixability, and good/bad examples per
rule. Rather than bend that contract, the rule library is a new, additive
catalogue (`RuleCatalogueResolver`, modeled on the existing
`AttackCatalogueResolver`) that a project can layer on top of whatever
`GuidelineStore` guidelines it already has installed. Wiring the resolved rule
text into the live review prompt (`ReviewRunner.PreparePromptAsync`) as an
additional `GLOBAL_GUIDELINES` contributor is the next increment — see
[Review integration](#review-integration) below for the exact call the wiring
will make.

## Directory layout

```
rules/
  README.md                 this document
  angular/
    QS-NG-001-....md         one file per rule
    ...
  dotnet/
    QS-DN-001-....md
    ...
```

Each rule is **one markdown file** so a rule's history is a normal git log for
that file, and a PR that changes one rule shows up as a one-file diff. The
compiled catalogue consumed at runtime
(`src/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json`) is
**generated**, not hand-edited — run:

```
node scripts/build-rule-catalogue.mjs
```

after adding or editing any `rules/**/*.md` file, and commit the regenerated
JSON alongside the source change. The build script validates every rule
against `schemas/quality-rule.v1.schema.json` before it writes the catalogue,
so a malformed rule fails the generation step instead of shipping silently.

## Rule id scheme

Stable ids have the form `QS-<AREA>-<NNN>`:

- `QS-NG-###` — Angular / frontend rules (`NG` for Angular).
- `QS-DN-###` — C# / .NET rules (`DN` for .NET).

Ids are permanent once published: a rule is deprecated (see `status` below),
never renumbered or deleted, so historical findings that cite an id remain
traceable. New areas (e.g. a future `QS-SEC-###` security-specific set) get
their own two-letter area code and their own numbering sequence.

## Rule file format

A rule file is YAML frontmatter followed by a markdown body with four
required sections.

```markdown
---
id: QS-NG-001
title: Style with design tokens, not ad-hoc values
technology: Angular
category: design-tokens
kinds: [code]
severity: medium
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

One or two sentences stating the rule as an instruction.

## Rationale

Why this matters, grounded in a concrete consequence (not "best practice"
hand-waving).

## Good example

```scss
/* code that follows the rule */
```

## Bad example

```scss
/* code that violates the rule, ideally a pattern actually seen in the wild */
```
```

### Frontmatter fields

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Stable id, see [Rule id scheme](#rule-id-scheme). |
| `title` | string | One-line summary shown in review context and tooling. |
| `technology` | string | `Angular` or `.NET` today; free text so new areas don't require a schema change. |
| `category` | string | Grouping used for docs and coverage reporting (e.g. `design-tokens`, `async-hygiene`). |
| `kinds` | string[] | Review kinds the rule applies to (`code`, `security`, `performance`). Almost always `[code]`. |
| `severity` | enum | `critical \| high \| medium \| low \| info` — the same vocabulary `quality-finding.v1.schema.json` uses for `severity`, so a finding produced from this rule can carry the rule's severity unchanged. |
| `autofixable` | boolean | Whether Quality Studio ships a deterministic fixer for the rule today. Every seed rule below is `false`: naming it honestly (rather than aspirationally) is the point — a rule marked `true` without a fixer wired into this codebase would be a silent lie the moment someone reads the catalogue and assumes tooling exists. Several rules (e.g. `QS-NG-007`, `QS-NG-009`) describe changes that are mechanically simple and could get a fixer later; `autofixable` only flips once that fixer actually exists here. |
| `tier` | enum | `core \| extended` — see [Default-on core vs project overrides](#default-on-core-vs-project-overrides). |
| `status` | enum | `active \| deprecated`. Deprecated rules stay in the catalogue (id stability) but are excluded from resolution. |
| `since` | date | `YYYY-MM-DD` the rule was published or last had a substantive (non-typo) change. |

## Default-on core vs project overrides

### Tiers

Every rule ships with `tier: core` or `tier: extended`:

- **`core`** rules are enabled for every project by default. In this seed set,
  `core` is assigned to every `critical` and `high` severity rule — defects
  severe enough that silence is itself a cost — plus two `medium` rules
  (`QS-NG-004`, `QS-NG-006`) that exist specifically to cover the
  operator-flagged "ad-hoc styles instead of design tokens / no component
  reuse" defect class: severity alone would leave them opt-in, but the
  mandate that created them was explicit that this defect class should be
  caught by default, not discovered later by a project that happened to turn
  the rule on.
- **`extended`** rules (`medium`, `low`, `info` severity in this seed set) are
  opt-in: a project turns one on deliberately via the override file below.

This split is the "DEFAULT-ON core" the operator asked for: a project gets
the core set with zero configuration, and everything else is an explicit,
reviewable choice.

### The override file

**File name:** `.quality/rules.json`, at the root of the reviewed repository.
It lives in and is versioned with the project's own repository — there is no
central per-project settings store, matching how `.quality/attacks/catalogue.json`
and `.quality/inputs/*.md` already work in this codebase.

**Scope:** v1 is repository-wide only — an override applies to every review of
every file in the repository. Path-scoped overrides (e.g. "extended rule X is
core inside `legacy/`") are a plausible future extension but are out of scope
for this seed; adding them later is a superset (a new optional `paths` field
on an override), not a breaking change.

**Schema:** `schemas/quality-rules-config.v1.schema.json`. Shape:

```json
{
  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
  "schemaVersion": 1,
  "overrides": [
    {
      "id": "QS-NG-003",
      "enabled": false,
      "reason": "Legacy admin module predates the token migration; tracked in QS-104."
    },
    {
      "id": "QS-DN-007",
      "severity": "high",
      "reason": "This team treats missing cancellation propagation as release-blocking."
    }
  ]
}
```

**Override syntax:** each entry in `overrides` names one rule `id` and sets
`enabled` and/or `severity` — whichever the project wants to change. At least
one of the two must be present, and `reason` is always required: an override
with no reason is exactly the kind of undocumented drift this file exists to
prevent. Overrides only adjust `enabled`/`severity`; they cannot rewrite a
rule's statement, rationale, or examples — a project that disagrees with the
rule's content should get it changed in this catalogue (a PR to the `.md`
file), not fork its meaning silently per project. An `id` that doesn't exist
in the built-in catalogue is a validation error (most likely a typo), not a
silently-ignored no-op.

Unlisted rules keep their catalogue default (`core` rules on at their
catalogue severity, `extended` rules off).

## Review integration

Rules feed the review prompt as **named context**, reusing the mechanism
`ReviewPromptBuilder` already has for `{{GLOBAL_GUIDELINES}}`: the prompt
template's existing instruction — "Guideline headings contain stable rule
ids... Set every finding's `ruleId` to the exact id of the supplied guideline
that caused it" (`prompts/file-code-review.v1.md`) — already tells the model
to key a finding's `ruleId` off a markdown heading, so a resolved rule
library renders as one `### QS-NG-001: <title>` section per active rule and
needs no template change to be honored.

`RuleCatalogueResolver.Resolve(repositoryRoot)` (see
`src/AgentOrchestrator.CodeQuality/RuleCatalogueResolver.cs`) resolves the
built-in catalogue against `.quality/rules.json`, and
`ResolvedRuleCatalogue.ToPromptMarkdown()` renders the active set in that
format. Wiring it into a live review is a one-line addition at the call site
that already accepts external guideline text —
`ReviewRunner.PreparePromptAsync` combines `request.GlobalGuidelines` with the
`InputResolver`-sourced guidelines before calling `ReviewPromptBuilder.Build`
— so the wiring point is
`request.GlobalGuidelines = resolver.Resolve(root).ToPromptMarkdown()`
wherever a `ReviewRequest` is constructed (`ReviewJobs.cs` today). This PR
ships the resolver and its tests but does not flip that switch in
`ReviewJobs.cs`: doing so changes the prompt (and therefore the prompt hash
and staleness cache key) for every review run in production, which deserves
its own change and its own before/after evidence rather than riding in on a
content PR.

`deterministicRuleIds`-style linkage from a rule to a machine sensor (so a
subset of autofixable rules could be enforced in the deterministic
pre-check wave ahead of model review, the same way
`AttackCatalogueEntry.DeterministicRuleIds` links an attack to a sensor) is
intentionally not modeled yet: every seed rule below is `autofixable: false`,
so there is nothing to enforce deterministically today. Add the field when
the first `autofixable: true` rule ships with a real fixer or sensor to point
at.

## Versioning

- Each rule file's own git history is its changelog; there is no separate
  changelog document to keep in sync.
- `since` is bumped whenever a rule's `statement` or examples change in a way
  that could change what a review flags. Fixing a typo does not bump it.
- Rule ids are permanent (see [Rule id scheme](#rule-id-scheme)). A rule that
  no longer applies is marked `status: deprecated` rather than deleted, so a
  finding produced against it historically still resolves to real rule text.
- `src/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json` carries
  its own `catalogueVersion` (semver), bumped by `scripts/build-rule-catalogue.mjs`
  whenever the set of rule ids or any entry's `severity`/`autofixable`/`tier`
  changes — the fields a resolver decision actually depends on.

## Policy: unresolved security findings

Per the 2026-08-12 operator decision: a review can surface a security finding
that the author does not fix before pushing. Blocking the push doesn't make
the underlying issue go away, and *also* leaving it unfixed and unrecorded
would be strictly worse than either fixing it or shipping with it visible.

**Decision:** the rule library and the review pipeline it feeds **document
the finding** — it stays visible in the finding record and the run's evidence
(`quality-finding.v1.schema.json`, `quality-run-report.v1.schema.json`) with
its `state` intact — and take **no enforcement action**: nothing in this
catalogue blocks a push or a merge on an open finding, regardless of
`severity` or `tier`. Enforcement (gating CI, requiring a disposition before
merge, etc.) is a distinct, separately-decided capability layered on top of
the finding data, not a side effect of a rule being `core`-tier or
`critical`-severity. If that capability is added later, this rule library is
exactly the place to record the exemption path for it (an override, or a
recorded `wontfix`/`accepted` finding disposition) — but until it exists,
"visible and recorded, not blocking" is the complete policy, stated here so
it isn't rediscovered as an accidental gap.

## Seed sets

- **Angular** (`rules/angular/`): component structure, design-token usage,
  standard-component reuse, template hygiene, change detection — ten rules,
  two per category. `QS-NG-003`/`QS-NG-004` (design tokens) and
  `QS-NG-005`/`QS-NG-006` (component reuse) exist specifically to cover the
  operator-observed defect class "ad-hoc styles instead of design tokens / no
  component reuse," and are grounded in real instances of that pattern in
  this repository's own frontend (see those files for exact `file:line`
  citations).
- **C#/.NET** (`rules/dotnet/`): API shape, DI patterns, async hygiene, test
  structure — eight rules, two per category, grounded in
  `src/QualityStudio.Api` and `tests/QualityStudio.Api.Tests`.

Both sets are grounded in the Quality Studio codebase itself, which is
directly readable from this workspace. The operator mandate also asked for
grounding in the Agent Studio codebase; that repository's source is not
checked out in this workspace (only wiki/workbench snapshots under
`/tmp/agent-studio/wiki-snapshots` are available, and they contain no
Angular or C# source). Extending these rules with Agent Studio-specific
`file:line` examples is a follow-up once that repository is reachable from a
QS task — the rules below stand on QS's own evidence and are not
technology-specific to QS, so they apply to Agent Studio's Angular/.NET code
unchanged in the meantime.
