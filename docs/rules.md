# Named rule library

Quality Studio ships its own named library of coding rules — the "this is
good code" reference reviews are checked against, and the thing review
findings' `ruleId` ultimately points back to. This closes the gap identified
by the [2026-08-18 quality concept dossier](operations/quality-concept/index.html#rules):
the guideline catalogue existed but was empty on every repository, so
findings could not cite a stable rule and the guideline-impact tooling had
nothing to weigh.

The library lives at [`rules/`](../rules/README.md) in this repository — one
Markdown file per rule, organized by technology (`rules/angular/`,
`rules/dotnet/`) — and is embedded into `AgentOrchestrator.CodeQuality` at
build time, so it travels with the package and applies to any repository
under review, not just this one.

## Rule format v1

Each rule is a single `rules/<technology>/<id>-<slug>.md` file: YAML
frontmatter, then a Markdown body. The frontmatter's normative logical shape
(after parsing) is [`schemas/rule.v1.schema.json`](../schemas/rule.v1.schema.json):

| Field | Meaning |
| --- | --- |
| `id` | Stable id, `QS-NG-NNN` (Angular) or `QS-CS-NNN` (.NET). Never reused once shipped. |
| `title` | One-line imperative statement of the rule. |
| `summary` | ≤300 character description, used in the installable catalogue. |
| `technology` | `angular` or `dotnet`. |
| `category` | Free-form slug (`design-tokens`, `async-hygiene`, ...). |
| `severity` | `critical` \| `high` \| `medium` \| `low` \| `info` — the same scale `finding.severity` uses. |
| `autofixable` | Whether a violation can be mechanically fixed without human judgment (not just deterministically *detected* — see below). |
| `defaultOn` | Whether the rule is part of the default-on core (see next section). |
| `kinds` / `levels` | Same enums as `.quality/inputs` guidelines (`code`/`security`/`performance`, `project`…`function`). |
| `version` | Semver (`MAJOR.MINOR.PATCH`); bump on any normative change to the rule. |
| `status` | `active` or `deprecated`. |
| `deterministicCheck` | Optional `{tool, ruleId}` cross-reference to an existing linter/analyzer rule that can flag (not necessarily fix) the same issue — see [Deterministic analyzer evidence](deterministic-analyzer-evidence.md) for how that evidence already reaches reviews. |

The body is free Markdown: `## Statement`, `## Rationale`, a `## Good
example` and `## Bad example` grounded in real code (a file path and line,
not a hypothetical, whenever the pattern already exists in a reviewed
repository), and a `## Changelog` with one dated bullet per `version` bump.
Rules are living documents — `version`/`status`/`## Changelog` are how a
rule's evolution stays visible without rewriting history; the id itself
never changes meaning once shipped, and a rule that no longer applies is
marked `status: deprecated`, not deleted (deleting it would silently orphan
any finding still citing it).

`autofixable` and `deterministicCheck` are independent: a rule can be
deterministically *detectable* (a linter flags it reliably) without being
safely *autofixable* (the correct fix needs human judgment — e.g. QS-NG-001
requires picking the right semantic token, QS-NG-005 changes runtime
behavior). Only mark `autofixable: true` when the fix itself is
unambiguous and safe to apply mechanically.

## Seed sets

Grounded in this repository and in Agent Studio's frontend
(`/home/agent/promotion/agent-studio` at the time these rules were written —
see each rule's Good/Bad examples for exact file:line citations).

**Angular** (`rules/angular/`) — covers the five areas the operator asked
for by name, including the specifically flagged defect class ("ad-hoc styles
instead of design tokens / no component reuse" → QS-NG-001 and QS-NG-002):

| Id | Category | Severity | Default-on |
| --- | --- | --- | --- |
| QS-NG-001 | design-tokens | high | yes |
| QS-NG-002 | standard-component-reuse | medium | yes |
| QS-NG-003 | component-structure | medium | no |
| QS-NG-004 | template-hygiene | medium | no |
| QS-NG-005 | change-detection | high | yes |

**.NET** (`rules/dotnet/`) — API shape, DI patterns, async hygiene, test
structure:

| Id | Category | Severity | Default-on |
| --- | --- | --- | --- |
| QS-CS-001 | api-shape | medium | no |
| QS-CS-002 | dependency-injection | medium | no |
| QS-CS-003 | async-hygiene | high | yes |
| QS-CS-004 | test-structure | low | no |

Default-on selection for this seed set: `severity: high` rules default on;
`medium`/`low` rules are opt-in. This is a starting policy, not a law —
future rules set `defaultOn` on their own merits and a project can always
override any rule's effective state (next section).

## Review integration

Rules reach reviews through the existing [review-input resolver](review-inputs.md),
which already precedes global guidelines with project guidelines by id. This
adds a third, first tier: **built-in** rules precede **global**, which
precede **project** — a same-id document at a higher-precedence scope
replaces the lower one entirely, exactly like the existing global/project
relationship. `InputResolver` only adds this tier when constructed with a
`RuleLibrary` (`new InputResolver(RuleLibrary.Default)`); every real call
site (`ReviewRunner`'s default construction, the CLI's `--explain-inputs`,
and the API's `InputResolver` registration) now does this, so the default-on
core applies automatically to every repository without a manual install
step. Tests that construct `new InputResolver()` directly are unaffected —
the built-in tier is opt-in per resolver instance.

Every rule surfaces in findings the same way an installed guideline does:
its (lower-cased) `id` becomes the finding's `ruleId`
(`docs/review-inputs.md`'s "supplied guidelines use their frontmatter id"
rule applies unchanged — a named rule *is* a guideline, just authored with
richer, versioned metadata). `QS-NG-001` → `ruleId: "qs-ng-001"`.

All 9 rules — default-on or not — also appear in the existing installable
catalogue (`GuidelineStore.Catalogue`, `GET /api/guidelines` /
`POST /api/repos/{id}/guidelines/catalog/{catalogueId}/install`). Installing
a rule writes it into `.quality/inputs/<id>.md` as an editable, always-
enabled guideline; that remains useful even for an already-default-on rule,
since installing gives a project its own editable copy to adjust wording
without touching the built-in tier.

`deterministicCheck` does not itself enforce anything: it documents that a
subset of rules already has a deterministic check available (Agent Studio's
`stylelint-declaration-strict-value`, `@angular-eslint/prefer-on-push-component-change-detection`,
`@angular-eslint/template/conditional-complexity`; Roslyn's `CA2016` for
QS-CS-003) that the existing [static-analysis wave](operations/static-analysis/index.html)
and [deterministic analyzer evidence](deterministic-analyzer-evidence.md)
pipeline can supply as corroborating, producer-neutral evidence alongside the
agent's review — wiring a specific analyzer into that pipeline is tracked
separately per rule, not duplicated here.

## Default-on core and project overrides

A rule's `defaultOn: true` means it applies to every repository
automatically, with no install step. A project adjusts that per rule with
`.quality/rules.json`, validated by
[`schemas/rules-config.v1.schema.json`](../schemas/rules-config.v1.schema.json):

```json
{
  "$schema": "https://agent-orchestrator.dev/quality/schemas/rules-config.v1.schema.json",
  "overrides": [
    { "ruleId": "QS-NG-002", "enabled": false, "reason": "Feature-folder duplication is deliberate in this repo." },
    { "ruleId": "QS-CS-001", "enabled": true }
  ]
}
```

Scope and syntax:

- **File**: `.quality/rules.json` at the repository root — JSON, versioned
  with the code, exactly like `.quality/scope.json` for review scope. No
  central per-project settings store.
- **Scope**: overrides only adjust the built-in rule library's effective
  enabled set. They never touch custom guidelines already installed under
  `.quality/inputs` — those keep the existing file-based `enabled: false`
  tombstone convention documented in [`review-inputs.md`](review-inputs.md).
- **Syntax**: `overrides` is an array of `{ ruleId, enabled, reason? }`.
  `ruleId` must reference a rule that exists in the library — an override for
  an unknown id is a validation error (a silently-ignored typo would mean a
  project believes a rule is off when it is not). Disabling a default-on
  rule (`enabled: false`) requires a `reason`, mirroring the existing
  `.quality/scope.json` requirement that an `exclude` rule states why.
  Enabling an opt-in rule (`enabled: true`) does not require a reason.
  Duplicate entries for the same `ruleId` are a validation error — there is
  no filesystem-order winner, same as duplicate guideline ids.

`RuleOverrideConfigurationStore` reads/writes this file; `RuleLibrary.ResolveDefaultOn(repositoryRoot)`
applies it on top of each rule's `defaultOn` to produce the effective
built-in set that `InputResolver` injects.

## Edge case: unfixed security findings

A security finding that stays unfixed is itself a risk signal, and silently
withholding a push over it would only convert one visible problem into a
second, hidden one (an unreviewed or un-pushed change). The stated policy
for this rule library and its findings, decided 2026-08-12, is:

- The finding **stays recorded and visible** — nothing about a named rule
  match, unfixed or not, is hidden from the review-meta sidecar, the
  finding-lifecycle history, or the UI.
- **No enforcement action is taken.** Named rules — including
  `security`-kind ones — do not block a push, a merge, or any other git
  operation on account of being unresolved.

This is a deliberate scope boundary, not an oversight: enforcement (gating)
is a separate, larger decision with its own failure modes (a blocked push
that cannot be overridden under time pressure creates its own incentive to
work around the tool). The rule library's job is to make the finding
correct, stable, and traceable to a named rule; whether and how to gate on
it is left to a future, explicitly scoped decision.
