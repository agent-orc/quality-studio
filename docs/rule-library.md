# Named rule library

Quality Studio owns a language-specific library of named statements about good code. It is distinct from repository guidelines: guidelines are free-form review context, while rules have stable identities, a validated shape, defaults, project overrides, finding attribution, and optional deterministic enforcement.

The library is file-first and English-only. Its source of truth is the [`rules/`](../rules/) tree:

```text
rules/
  angular/QS-NG-001.json ... QS-NG-005.json
  dotnet/QS-CS-001.json  ... QS-CS-004.json
```

The core package embeds these files for standalone use. Editing the C# catalogue in isolation is not supported: every built-in rule change starts in its JSON file and is reviewed like source code.

## Rule contract

[`quality-rule.v1.schema.json`](../schemas/quality-rule.v1.schema.json) is the normative JSON Schema. Every file contains:

| Field | Meaning |
| --- | --- |
| `id` | Immutable, stable `QS-<language>-<number>` identity used by findings. |
| `version` | Semantic document version. It changes when the rule meaning, examples, severity, or applicability changes. |
| `title`, `language`, `category` | Named catalogue and routing metadata. |
| `statement` | The behavior required by the rule. |
| `rationale` | Why the behavior matters in Agent Studio and Quality Studio. |
| `severity` | Default finding severity: `critical`, `high`, `medium`, `low`, or `info`. |
| `autofixable` | Capability declaration only. `true` does not authorize an edit and does not imply that an autofixer currently exists. |
| `defaultEnabled` | Membership in the default-on core. |
| `reviewKinds`, `appliesTo` | Review-lane and repository-relative glob applicability. |
| `examples.bad`, `examples.good` | Concrete counterexample and preferred form. |
| `history[]` | Append-only version, date, and change account; it must contain the current version. |

Rule IDs are never recycled or renamed. Retire a rule by making a new library version and setting `defaultEnabled` to `false`; retain its file and history so historical findings remain intelligible. Text-only clarifications increment patch, a materially expanded or reduced requirement increments minor, and a semantic replacement requires a new rule ID. Rule file history complements Git history rather than replacing it.

## Default-on core and project overrides

All nine v1 seed rules are the initial default-on core. A project may adjust an individual effective rule only in the versioned repository file `.quality/rules.json`; Quality Studio has no central per-project rule-settings store. The normative configuration schema is [`quality-rules-config.v1.schema.json`](../schemas/quality-rules-config.v1.schema.json).

```json
{
  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
  "schemaVersion": 1,
  "overrides": {
    "QS-NG-002": { "severity": "medium" },
    "QS-NG-003": { "enabled": false }
  }
}
```

Scope semantics are deterministic:

1. Start with the library's `defaultEnabled` value.
2. Apply the matching ID's project `enabled` and/or `severity` values.
3. Select only rules whose `reviewKinds` includes the review kind and whose `appliesTo` matches at least one reviewed repository-relative path.
4. Sort the effective set by stable ID.

An absent configuration means no overrides. An empty `overrides` object explicitly accepts the defaults. Unknown rule IDs, unknown JSON properties, unsupported schema versions, and invalid severities fail visibly instead of being ignored. Overrides change application, not the library document or its version. Aggregate reviews receive the union applicable to their descendant files.

## Seed sets and grounding

The first rules were derived from current patterns and defect history in both products, not from a generic style checklist. Quality Studio grounding includes `frontend/src/styles.css`, `project-dashboard.ts`/`.html`, `ReviewJobs.cs`, public API records, cancellation-aware core services, and temporary-repository xUnit fixtures. Agent Studio grounding includes its `frontend/AGENTS.md`, semantic spacing tokens, standalone feature/component folders, shared `count-badge`, `list-row`, `pane-header`, dialog and async-feedback components, signal state, constructor-injected backend services, and behavior-named isolated tests.

| Rule | Seed concern | Default severity | Deterministic pre-check |
| --- | --- | --- | --- |
| `QS-NG-001` | Focused component structure and external templates/styles | medium | — |
| `QS-NG-002` | Central design tokens; no ad-hoc component visual values | high | Raw non-zero `px` in component CSS/SCSS |
| `QS-NG-003` | Standard-component and shared-primitive reuse | high | —; reuse requires contextual judgment |
| `QS-NG-004` | Declarative, typed, semantic template hygiene | medium | Inline `style=` attributes |
| `QS-NG-005` | `OnPush` and explicit reactive state | medium | `@Component` without `OnPush` |
| `QS-CS-001` | Explicit, narrow API shape | high | — |
| `QS-CS-002` | Constructor injection and explicit lifetime | high | — |
| `QS-CS-003` | Cancellation-aware, non-blocking async flows | high | `.Result` and `.Wait()` |
| `QS-CS-004` | Behavior-focused, isolated test structure | medium | — |

`QS-NG-002` and `QS-NG-003` explicitly cover the operator-observed defect class: ad-hoc styles in place of the design-token system and local UI inventions in place of standard components.

## Review and static-analysis integration

```text
rules/*.json + .quality/rules.json + reviewed paths
                         |
                         v
                  effective rule set
                    /           \
                   v             v
         named prompt context   quality-rules sensor
                   \             /
                    v           v
                findings with exact ruleId
                         |
                         v
       review sidecar: rule IDs + content hashes + evidence
```

The review prompt includes each effective rule by name, version, effective severity, statement, rationale, and examples. Agent findings must copy the exact `QS-...` ID into `ruleId`. Effective rule content participates in the review-input hash, so a rule or override change makes an otherwise-current review show policy drift. Review sidecars record the effective rule IDs and content hashes under `reviewInputs.rules`.

Named core rules are outside the configurable free-form guideline character budget: a project cannot accidentally truncate a default-on rule by adding prose guidelines. Their content is nevertheless part of the same effective policy hash.

The built-in `quality-rules` deterministic sensor is enabled in the API sensor catalogue and in CLI code/performance reviews. It emits ordinary deterministic evidence with producer provenance and the same named rule ID. It intentionally implements only mechanically defensible checks. The agent judges contextual concerns such as component reuse, API design, and whether an exceptional visual value is justified. Project disable/severity overrides affect both prompt context and the static wave.

Deterministic evidence stays separate from agent-authored findings and grades. The sensor does not edit code. `autofixable` is reserved for a future explicit fix workflow.

## Unfixed security finding and push policy

An unresolved security finding always remains visible and recorded in repository-owned review evidence. Withholding a push while also leaving the finding unfixed is itself harmful: it hides the state from normal collaboration and can strand both the vulnerable change and its audit trail in a local workspace.

Current policy: **Quality Studio takes no push-blocking action for an unfixed security finding.** It records the finding, state, severity, evidence, and subsequent disposition, but neither this rule library nor its deterministic wave blocks a push. The rationale is to preserve visibility and avoid a deadlock in which withholding the push does not remediate the issue. Any future enforcement policy requires a separate operator decision and must distinguish remediation, acceptance, and escalation. Git commit/push orchestration remains outside the rule library.

## Evolving the library

For every change:

1. Change the rule file, increment its version, and append a `history` entry.
2. Keep the stable ID unless the semantic contract is being replaced.
3. Update or add library, prompt, override, and sensor tests as applicable.
4. Update this dossier when defaults, configuration semantics, seed coverage, or enforcement changes.
5. Validate both JSON schemas and run the .NET test suite.

Historical review sidecars keep their rule content hashes, while Git retains the exact prior rule document. This lets a reviewer distinguish source-code staleness from a living-rule policy change.
