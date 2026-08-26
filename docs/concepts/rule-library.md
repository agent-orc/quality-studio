# Quality Studio rule library

Quality Studio's review guidance was, before this card, four broad,
hand-written blobs of prose (`dotnet-api-safety`, `angular-typescript`,
`testing-confidence`, `security-boundaries` in `GuidelineStore.Catalogue`).
A finding could cite one of those four ids at most - never anything more
specific. This is the rule library: a named, versioned, file-first set of
best-practice rules that plug into the exact same guideline mechanism, one
rule at a time, so a finding can cite `QS-NG-003` instead of just
`angular-typescript`.

## Format

Every rule is one JSON file, validated against
[`schemas/rule.v1.schema.json`](../../schemas/rule.v1.schema.json):

```
rules/
  angular/QS-NG-001.json ... QS-NG-010.json
  dotnet/QS-DN-001.json ... QS-DN-008.json
```

Required fields: `id` (stable, `QS-<TECH>-<NNN>`, never reused or
renumbered - a retired rule is marked `status: "deprecated"` with
`supersededBy`, not deleted), `technology`, `category`, `title`,
`statement` (the directive itself - what a reviewer cites), `rationale`
(why it exists), `severity` (matches the finding envelope's severity enum),
`autofixable`, `defaultOn`, `kinds`/`levels` (same vocabulary
`InputResolver` already uses), a `good` and a `bad` code example, `version`,
and an append-only `changelog`. See [`rules/README.md`](../../rules/README.md)
for the directory contract.

A rule file's own git history is its primary change record; the
`changelog` array is a compact, in-file summary of the same thing for
readers who don't want to run `git log` on one file.

## Seed sets

Grounded in this repository's own `frontend/src/app` (Angular) and
`src/AgentOrchestrator.CodeQuality` / `src/QualityStudio.Api` (.NET). Real
excerpts carry a `source` path; target-state examples are explicitly
captioned as illustrative. Bad examples are either existing defects or a
specific contrast against a codebase convention. `sourceRepository`
defaults to `quality-studio`; cross-repository evidence names
`agent-studio` explicitly.

The seed was also checked against the current Agent Studio codebase. Its
`frontend/AGENTS.md` and `frontend/src/styles/_tokens-semantic.scss` define
the spacing-token contract behind QS-NG-003/004. Its shared
`components/dialog/DialogComponent` and `components/count-badge/CountBadgeComponent`
are the concrete good examples in QS-NG-005/006. Its Angular components use
standalone components, signals, built-in template control flow and OnPush,
while its backend request and persistence paths propagate cancellation and
use `ConfigureAwait(false)`. The rules therefore encode conventions shared
by Agent Studio and Quality Studio, while the documented bad examples expose
specific Quality Studio defects.

**Angular** (`rules/angular`, 5 categories, 2 rules each):
component-structure (QS-NG-001/002), **design-tokens**
(QS-NG-003/004), **component-reuse** (QS-NG-005/006), template-hygiene
(QS-NG-007/008), change-detection (QS-NG-009/010).

The operator-observed defect class - *ad-hoc styles instead of design
tokens, no component reuse* - is exactly `design-tokens` and
`component-reuse`. QS-NG-003's bad example is a real excerpt from
`frontend/src/app/app.css` (raw `px` sizing and a raw hex gradient) next to
the same file's `--studio-*` token usage elsewhere. QS-NG-005/006's bad
examples are the app's own duplicated dialog-backdrop and badge/chip CSS,
found across `app.html`, `editor.css`, `explorer.css`, and `app.css`. All
four rules are `defaultOn: true` for exactly this reason.

**.NET** (`rules/dotnet`, 4 categories, 2 rules each): api-shape
(QS-DN-001/002), dependency-injection (QS-DN-003/004), async-hygiene
(QS-DN-005/006), test-structure (QS-DN-007/008) - grounded in `Program.cs`'s
paired-route and DI-lifetime conventions and `ReviewRunner.cs`'s
`ConfigureAwait(false)`/`CancellationToken` propagation.

## Review integration

**Citation.** `GuidelineStore.Catalogue` is now the four legacy entries
plus `RuleLibrary.CatalogueEntries` - one entry per rule, generated from the
JSON at load time (`RuleLibrary.cs`, embedded the same way `prompts/` and
`catalogues/` already are). Installing a rule
(`POST /api/guidelines/catalog/QS-NG-003/install`, unchanged endpoint)
writes an ordinary `.quality/inputs/QS-NG-003.md` file. `InputResolver`
renders it under a `## QS-NG-003` heading exactly like any other guideline,
and the review prompt's existing instruction - *"Guideline headings contain
stable rule ids. Set every finding's `ruleId` to the exact id of the
supplied guideline that caused it"* - now resolves to the specific rule,
not a technology bucket. No prompt template changed.

**Default-on core.** Every `defaultOn: true` rule applies to every project
without onboarding, installation, or repository writes. `InputResolver`
loads the embedded library on every resolution and includes each active rule
whose effective enabled state (`.quality/rules.config.json` override,
falling back to the rule's own `defaultOn`) matches the review kind and
level. The default-on core is QS-NG-003/004/005/006/009 and
QS-DN-002/004/005/006.

Automatic prompt context contains the id, title, statement, rationale,
severity and autofixable flag. Good and bad examples remain in the rule file
and in explicitly installed catalogue copies, but are not repeated in every
prompt; this preserves the review-input budget for project-specific guidance.

An explicitly installed `.quality/inputs/<id>.md` with the same id replaces
the library copy, using the existing project-over-global guideline
semantics. A config override with `enabled: false` disables both the
library copy and an installed copy with that rule id. Disabled and replaced
rules remain visible in input-resolution omissions, so `--explain-inputs`
can account for the effective policy. A severity override changes both the
severity rendered into named prompt context and its priority in the input
budget. Because resolution is read-only, reviewing code never creates or
rewrites project files.

**Deterministic pre-check.** `SarifSensor`
(`src/AgentOrchestrator.CodeQuality/SarifSensor.cs`) already runs *"an
optional repository-owned command and ingests its SARIF 2.1.0 report"*:
this is the static-analysis wave extension point, and it needed no code
change. `scripts/rule-checks/design-tokens-check.mjs` scans CSS for raw hex
colors and raw `px` literals that exactly duplicate a `:root` custom-property
token (the provable subset of QS-NG-003), plus the same literal repeated
across stylesheets (QS-NG-004), and writes a SARIF report citing those rule
ids. Register it as a deterministic sensor per project via
`ReviewSensorConfiguration { Id = "sarif", Configuration = { command =
"node scripts/rule-checks/design-tokens-check.mjs {target} {reportPath}",
reportPath = ".quality/rule-checks/design-tokens.sarif.json" } }` - the
review prompt then sees its results as prior deterministic evidence, kept
distinct from the agent's own judgement (`ReviewPromptBuilder`'s existing
"prior machine-produced facts, not conclusions" framing). Findings from the
fixture in `tests/AgentOrchestrator.CodeQuality.Tests/Fixtures/sarif/
design-tokens.sarif.json` round-trip through `SarifSensor.ParseAsync` citing
real `RuleLibrary` ids (`SarifSensorTests.
DesignTokensCheckFixture_CitesRealRuleLibraryIds`). Only two of the
eighteen rules have a deterministic check today; the rest rely on the
review agent, same as every guideline before this card.

## Versioning

A rule's `version` (semver) and `changelog` cover its own content; nothing
tracks a library-wide version number because rules evolve independently.
An explicitly installed catalogue copy can lag the embedded library version;
automatic default context always uses the package's current rule. Bump
`version` and add a `changelog` entry on any
change to `statement`, `severity`, or the examples; typo fixes in
`rationale` don't require a bump. Deprecate with `status: "deprecated"` and
`supersededBy` rather than deleting a shipped id, so historical findings
continue to resolve to the rule that produced them.

## `.quality/rules.config.json`

Optional, versioned with the code like any other reviewed file - no
central per-project settings store
([`schemas/rule-config.v1.schema.json`](../../schemas/rule-config.v1.schema.json)):

```json
{
  "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
  "schemaVersion": 1,
  "overrides": {
    "QS-NG-003": { "enabled": false, "reason": "enforced by an external stylelint config instead" }
  }
}
```

It lists **deviations only** - the default-on set itself is not repeated
here, and an absent file means every `defaultOn` rule applies unmodified.
`enabled: false` disables a rule for this project; `enabled: true` opts a
normally off-by-default rule into automatic review context. A `severity`
override changes the severity and budget priority rendered for that project.
It supplies the project default for findings under that rule; reviewers can
still raise or lower a concrete finding when its actual impact warrants it.
`reason` is not schema-enforced but is expected in review, the same way any
other override needs to be explainable. Unknown rule ids, empty overrides,
unsupported schema versions, and invalid severities fail resolution instead
of being silently ignored.

## Edge case: an unfixed security finding

A `security`-kind finding can sit `Open` (or `Accepted`, meaning
"acknowledged, not fixed yet" - see `FindingState` in
`FindingStateStore.cs`) indefinitely. Two failure modes are both bad:
silently blocking the push leaves a team stuck on an unrelated change
because of an old, possibly-disputed finding; silently letting the finding
disappear from view is worse. Withholding the push while leaving the finding
unfixed is itself a second problem, not a remediation. The policy this card
ships:

- **No enforcement.** Nothing in this repository blocks a commit or push on
  finding state - the rule library adds none, and none existed before it.
  A `.quality/rules.config.json` override can turn a rule off going
  forward, but it cannot retroactively hide or clear findings a rule
  already produced.
- **Documented, not hidden.** The finding stays exactly where every other
  finding lives: in the review meta sidecar next to the code, in whatever
  state (`open`/`accepted`/`waived`/`false-positive`) a reviewer put it in
  through the existing review-panel disposition flow. It is git history:
  diffable, visible in the next review, and visible to whoever reads the
  file next.
- **Why this and not the alternative.** Quality Studio's stated job is to
  keep quality truth honest and next to the code, not to be a merge gate;
  Agent Studio owns task/PR flow ("Handover" in
  [`handover.md`](handover.md)). A push-blocking gate here would put an
  enforcement decision in the wrong product, and - per the incident this
  policy exists to avoid - would trade one problem (an unresolved finding)
  for a worse one (an unresolved finding *and* a blocked team). If a
  project wants a hard gate, that decision and its blast radius belong in
  the CI pipeline that owns the merge, not silently inside the rule
  library.

This is a decision worth revisiting if a project asks for enforcement, but
it should be an explicit, opt-in CI policy change outside this library, not
a default this library assumes.
