# Quality Studio rule library

Quality Studio's review guidance was, before this card, four broad,
hand-written blobs of prose (`dotnet-api-safety`, `angular-typescript`,
`testing-confidence`, `security-boundaries` in `GuidelineStore.Catalogue`).
A finding could cite one of those four ids at most — never anything more
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
renumbered — a retired rule is marked `status: "deprecated"` with
`supersededBy`, not deleted), `technology`, `category`, `title`,
`statement` (the directive itself — what a reviewer cites), `rationale`
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
`src/AgentOrchestrator.CodeQuality` / `src/QualityStudio.Api` (.NET) — every
`good` example is a real excerpt with a `source` path; every `bad` example
is either a real excerpt of an existing defect or an explicit contrast
against one.

**Angular** (`rules/angular`, 5 categories, 2 rules each):
component-structure (QS-NG-001/002), **design-tokens**
(QS-NG-003/004), **component-reuse** (QS-NG-005/006), template-hygiene
(QS-NG-007/008), change-detection (QS-NG-009/010).

The operator-observed defect class — *ad-hoc styles instead of design
tokens, no component reuse* — is exactly `design-tokens` and
`component-reuse`. QS-NG-003's bad example is a real excerpt from
`frontend/src/app/app.css` (raw `px` sizing and a raw hex gradient) next to
the same file's `--studio-*` token usage elsewhere. QS-NG-005/006's bad
examples are the app's own duplicated dialog-backdrop and badge/chip CSS,
found across `app.html`, `editor.css`, `explorer.css`, and `app.css`. All
four rules are `defaultOn: true` for exactly this reason.

**.NET** (`rules/dotnet`, 4 categories, 2 rules each): api-shape
(QS-DN-001/002), dependency-injection (QS-DN-003/004), async-hygiene
(QS-DN-005/006), test-structure (QS-DN-007/008) — grounded in `Program.cs`'s
paired-route and DI-lifetime conventions and `ReviewRunner.cs`'s
`ConfigureAwait(false)`/`CancellationToken` propagation.

## Review integration

### Citation

`GuidelineStore.Catalogue` is now the four legacy entries
plus `RuleLibrary.CatalogueEntries` — one entry per rule, generated from the
JSON at load time (`RuleLibrary.cs`, embedded the same way `prompts/` and
`catalogues/` already are). Installing a rule
(`POST /api/guidelines/catalog/QS-NG-003/install`, unchanged endpoint)
writes an ordinary `.quality/inputs/QS-NG-003.md` file. `InputResolver`
renders it under a `## QS-NG-003` heading exactly like any other guideline,
and the review prompt's existing instruction — *"Guideline headings contain
stable rule ids. Set every finding's `ruleId` to the exact id of the
supplied guideline that caused it"* — now resolves to the specific rule,
not a technology bucket. No prompt template changed.

### Default-on resolution and optional sync

Every `defaultOn: true` rule applies to every project without a manual
install step. The core is 9 of the 18 seeded rules — a rule earns a place in
it only if it is technology-wide (not a house style one project may
reasonably reject) and cheap to judge from the diff alone, so it stays quiet
on healthy code:

| | Default-on |
| --- | --- |
| Angular | QS-NG-003, QS-NG-004 (design-tokens), QS-NG-005, QS-NG-006 (component-reuse), QS-NG-009 (OnPush) |
| .NET | QS-DN-002, QS-DN-006 (cancellation), QS-DN-004 (no service locator), QS-DN-005 (`ConfigureAwait(false)`) |

The other nine are opt-in because they encode a choice a project is
entitled to make differently — file layout (QS-NG-002), signals over RxJS
(QS-NG-010), paired route mapping (QS-DN-001), test conventions
(QS-DN-007/008) — or, in QS-NG-008's case, because judging whether a
`@for` list is reorderable needs more context than the rule can assume,
despite its `high` severity when it does fire.

`InputResolver` computes the effective set on every resolution: a
`.quality/rules.config.json` override wins, otherwise the rule's own
`defaultOn` applies. It adds active, applicable rules directly to the
project-guideline context under their stable ids. This is the automatic
path used by API and CLI reviews; it does not require an onboarding action
and does not dirty the reviewed repository with generated copies. An
explicit `.quality/inputs/<id>.md` file wins over the library rendering for
the same id. The JSON config remains authoritative: `enabled: false`
disables both the library rendering and a previously materialized copy, and
a severity/reason adjustment is appended to an explicit copy's context.

Automatic prompt context contains each rule's id, title, statement,
rationale, effective severity, autofixability, version, and override reason.
The full good/bad examples remain in the canonical rule file and in an
explicitly installed/materialized guideline. This compact rendering keeps
the complete core within the prompt budget; repository-authored inputs are
budgeted before library defaults so shipped guidance cannot crowd out a
project's intentional local policy.

`GuidelineStore.SyncDefaultRules(repositoryRoot)` remains available when a
team wants visible, editable materializations under `.quality/inputs/`. It
installs, updates, or removes those files to match the same effective set
and is idempotent — call it again and unchanged rules report
`"unchanged"`. Reach it through
`POST /api/guidelines/sync-defaults` (and the paired
`/api/repos/{repoId}/...` route). Review correctness never depends on this
optional endpoint: automatic resolution always uses the current library
and project config.

Explicit sync **overwrites** the installed file's content, priority,
kinds, and levels to match the current rule every time it runs. A synced
file is not meant to be hand-edited in place — if a project needs different
wording, that is what an override plus a separately-installed, differently-
named custom guideline is for. This mirrors how the rest of `.quality/` is
already repo-owned and machine-refreshed (e.g. `boundaries/inventory.json`).

### Deterministic pre-checks

`SarifSensor`
(`src/AgentOrchestrator.CodeQuality/SarifSensor.cs`) already runs *"an
optional repository-owned command and ingests its SARIF 2.1.0 report"* —
this is the static-analysis wave extension point, and it needed no code
change. `scripts/rule-checks/design-tokens-check.mjs` learns the literal
values represented by `--studio-*`/`--font-*` declarations, scans CSS for
raw hex colors and raw `px` values that duplicate that vocabulary
(QS-NG-003), and detects the same raw literal repeated across stylesheets
(QS-NG-004). It writes a SARIF report citing those rule ids. Novel one-off
values remain a review judgement rather than a deterministic QS-NG-003
finding. Register it as a deterministic sensor per project via
`ReviewSensorConfiguration { Id = "sarif", Configuration = { command =
"node scripts/rule-checks/design-tokens-check.mjs {target} {reportPath}",
reportPath = ".quality/rule-checks/design-tokens.sarif.json" } }` — the
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
tracks a library-wide version number, because rules install and sync
individually — a project can be on QS-NG-003 v1.2.0 while a rule it never
enabled sits at v1.0.0. Bump `version` and add a `changelog` entry on any
change to `statement`, `severity`, or the examples; typo fixes in
`rationale` don't require a bump. Deprecate with `status: "deprecated"` and
`supersededBy` rather than deleting a shipped id — `SyncDefaultRules` still
needs to be able to remove a deprecated rule's installed file cleanly from
projects that had it on.

## `.quality/rules.config.json`

Optional, versioned with the code like any other reviewed file — no
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

It lists **deviations only** — the default-on set itself is not repeated
here, and an absent file means every `defaultOn` rule applies unmodified.
`enabled: false` disables a default rule for this project; `enabled: true`
can also opt a normally off-by-default rule into automatic resolution. A
`severity` override changes the rule's prompt priority and is rendered next
to the library default and the optional `reason`. It remains guidance: the
review prompt still asks the agent for its own concrete severity judgement,
so an override is not a hard cap or floor on findings. Unknown rule ids,
unknown fields, unsupported severities, and unsupported schema versions are
rejected when the config is loaded. `reason` is not schema-enforced but is
expected in review, the same way any other override needs to be explainable.

Scope is per repository: the config affects only reviews whose repository
root contains that exact `.quality/rules.config.json`. There is no central
per-project settings record, inheritance from parent directories, or
cross-repository state. Overrides list deviations only; omitted rules keep
their shipped defaults.

## Edge case: an unfixed security finding

A `security`-kind finding can sit `Open` (or `Accepted`, meaning
"acknowledged, not fixed yet" — see `FindingState` in
`FindingStateStore.cs`) indefinitely. Two failure modes are both bad:
silently blocking the push leaves a team stuck on an unrelated change
because of an old, possibly-disputed finding; silently letting the finding
disappear from view is worse. The policy this card ships:

- **No enforcement.** Nothing in this repository blocks a commit or push on
  finding state — the rule library adds none, and none existed before it.
  A `.quality/rules.config.json` override can turn a rule off going
  forward, but it cannot retroactively hide or clear findings a rule
  already produced.
- **Documented, not hidden.** The finding stays exactly where every other
  finding lives: in the review meta sidecar next to the code, in whatever
  state (`open`/`accepted`/`waived`/`false-positive`) a reviewer put it in
  through the existing review-panel disposition flow. It is git history —
  diffable, visible in the next review, and visible to whoever reads the
  file next.
- **Why this and not the alternative.** Quality Studio's stated job is to
  keep quality truth honest and next to the code, not to be a merge gate;
  Agent Studio owns task/PR flow ("Handover" in
  [`handover.md`](handover.md)). A push-blocking gate here would put an
  enforcement decision in the wrong product, and — per the incident this
  policy exists to avoid — would trade one problem (an unresolved finding)
  for a worse one (an unresolved finding *and* a blocked team). If a
  project wants a hard gate, that decision and its blast radius belong in
  the CI pipeline that owns the merge, not silently inside the rule
  library.

This is a decision worth revisiting if a project asks for enforcement, but
it should be an explicit, opt-in CI policy change outside this library, not
a default this library assumes.
