# Named rule library

Quality Studio owns a language-specific library of named engineering rules. The source of truth is the English, file-first [`rules/`](../rules/) tree. Rules are not hidden in a central settings service and are distinct from free-form review inputs in `.quality/inputs/`.

## Rule format

Each rule is one JSON document named after its stable id, for example `rules/angular/QS-NG-002.json`. The contract is [`quality-rule.v1.schema.json`](../schemas/quality-rule.v1.schema.json).

| Field | Meaning |
| --- | --- |
| `id` | Stable, never-reused identity in the form `QS-<family>-<number>`. Angular uses `QS-NG-*`; C#/.NET uses `QS-CS-*`. |
| `version` | Semantic version of the rule document. Increment it when normative meaning changes. |
| `name` | Short human-readable name shown in prompts and findings. |
| `language`, `category`, `appliesTo` | Language family, concern, review kinds, and source extensions to which the rule applies. Angular rules additionally require an `angular.json` in the reviewed file's repository ancestry. |
| `statement` | Normative description of good code. |
| `rationale` | Why the practice matters in Agent Studio and Quality Studio. |
| `badExample`, `goodExample` | English-context code examples with an explicit fence language. |
| `severity` | Default finding severity: `critical`, `high`, `medium`, `low`, or `info`. |
| `autofixable` | Whether Quality Studio may safely offer an automatic edit. The seed rules are deliberately `false`; choosing an equivalent token or shared component needs context. |
| `defaultOn` | Whether the rule belongs to the default-on core applied to every repository. |
| `deterministicCheck` | Optional pre-check implementation id. Its findings use the same rule id. |
| `history` | Append-only dated change notes. Git remains the full line-level history. |

The library files are embedded in the core package at build time. This preserves file-first authorship in this repository while making the same versioned defaults available when the package reviews another checkout. Duplicate ids, unknown fields, malformed ids, and incomplete documents fail library loading.

## Seed sets

The initial rules are grounded in the current Quality Studio and Agent Studio conventions: Angular feature folders, signal-backed view state, central visual tokens and shared component families; .NET boundary records, composition-root registration, cancellation-aware orchestration, and isolated contract tests.

| Rule | Concern | Default | Deterministic pre-check |
| --- | --- | --- | --- |
| `QS-NG-001` | Focused, colocated component structure | Off | — |
| `QS-NG-002` | Central design tokens; no ad-hoc spacing, color, radius, or badge geometry | On | Raw non-token pixel/color values in component CSS/SCSS; central `src/styles.*` token definitions are excluded |
| `QS-NG-003` | Standard-component reuse before local primitives | On | —; requires semantic review |
| `QS-NG-004` | Declarative template hygiene | On | Inline `style` attributes |
| `QS-NG-005` | Explicit reactive change-detection inputs | On | —; requires semantic review |
| `QS-CS-001` | Explicit, transport-safe API shape | On | — |
| `QS-CS-002` | Constructor injection and explicit lifetimes | On | — |
| `QS-CS-003` | Cancellation-aware, non-blocking async flow | On | `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, and `async void` |
| `QS-CS-004` | One observable contract per test | Off | — |

`QS-NG-002` and `QS-NG-003` explicitly cover the operator-observed defect class: ad-hoc styles in place of design tokens and creating local UI primitives instead of reusing standard components.

## Repository-owned configuration

The only project override file is `.quality/rules.json`, validated by [`rule-configuration.v1.schema.json`](../schemas/rule-configuration.v1.schema.json). It is ordinary repository content: versioned, reviewed, and transported with the code. Quality Studio does not maintain a central per-project rule settings store.

```json
{
  "$schema": "https://quality.studio/schemas/rule-configuration.v1.schema.json",
  "schemaVersion": 1,
  "rules": {
    "QS-NG-002": { "severity": "high" },
    "QS-NG-005": { "enabled": false },
    "QS-CS-004": { "enabled": true }
  }
}
```

Scope semantics are intentionally simple in v1:

- The file applies to the entire repository rooted at the checkout containing `.quality/rules.json`.
- A rule is considered only when its review kind, source extension, and technology applicability match the current subject.
- With no override, `defaultOn` is authoritative. Thus the core subset is automatically enabled for every project, including projects with no configuration file.
- `enabled` turns one named rule on or off for this repository. `severity` replaces only its default severity. Both may be supplied together.
- An override for an unknown id, an unknown property, an invalid severity, or an empty override is a visible configuration error. It is never silently ignored.
- Disabling a rule removes it from named prompt context and its deterministic pre-check. Agent output that still claims a disabled or inapplicable `QS-*` id is rejected.

Path-specific exceptions are deliberately not part of v1. Repository scope exclusions remain in `.quality/scope.json`; finding dispositions remain the mechanism for accepted or waived instances. This avoids making the normative rule configuration double as a suppression language.

This repository's own [`.quality/rules.json`](../.quality/rules.json) enables the two opt-in rules for component structure and test structure, demonstrating that project policy is checked in beside the code.

## Review and static-analysis integration

Before a review prompt is built, Quality Studio resolves the applicable defaults and repository overrides. The prompt receives each enabled rule by exact id, name, effective severity, autofixable flag, statement, rationale, and examples. The effective review-input hash includes the resolved rule versions, content hashes, and severity overrides, so a rule edit or override produces `policyDrift` rather than pretending an old review is current.

Review sidecars record the resolved rules under `reviewInputs.rules`. Every agent finding already requires `ruleId`; a violation of a named rule must use the exact `QS-*` id. Deterministic evidence stays separate from agent-authored findings and uses the same stable id for correlation.

The first static-analysis wave is available through:

```shell
dotnet run --project src/quality-cli -- rules check .
```

Exit code `0` means no deterministic rule findings, `1` means findings were recorded, and `2` means the scan or configuration was invalid. The API registers the `quality-rules` deterministic sensor, runs it once per review wave, and projects its facts onto reviewed subjects. These checks are a deliberately precise subset; rules requiring component or architectural judgement remain prompt-only.
Repeated textual matches for the same rule on one source line are consolidated into one finding so minified or compact source does not flood review context.

## Versioning and change policy

Rule ids are permanent and never reassigned. Compatible editorial clarification increments the patch version; new or materially broader enforcement increments the minor version; incompatible meaning requires a new rule id (a major version is not used to repurpose an id). Every normative edit adds a `history` entry. Rule file changes, project overrides, schema changes, and review sidecars are all normal Git history.

The schema version governs document shape independently from a rule's semantic version. A future schema is introduced as a new schema file and loader path; v1 files are not rewritten implicitly.

## Unfixed security findings and pushes

Current policy explicitly takes **no push-blocking action** for a security finding that remains unfixed. The finding stays visible and recorded in repository-owned review metadata and reports, with its lifecycle state and evidence intact.

This is an acknowledged edge case: withholding a push while also leaving the security finding unfixed is itself harmful because the remediation and the durable finding record may never reach the shared repository. Automatically allowing or blocking a push cannot resolve that ownership failure. For now Quality Studio therefore documents and preserves the finding, but neither the rule library nor the worker CLI blocks, withholds, commits, or pushes code. A later policy may introduce an explicit platform-owned gate only after remediation ownership and recovery behavior are defined.
