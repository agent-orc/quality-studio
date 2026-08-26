# Named best-practice rule library

Quality Studio owns a language-specific, file-first library of named statements about good code. The canonical rule documents live under [`rules/`](../rules/); they are product inputs, not centrally managed project settings. Every rule has a stable ID, and every finding caused by a named rule uses that exact ID in `ruleId`.

## Rule contract

Each `rules/<language>/<id>.rule.json` document validates against [`quality-rule.v1.schema.json`](../schemas/quality-rule.v1.schema.json). Version 1 requires:

| Field | Meaning |
| --- | --- |
| `id` | Permanent identity such as `QS-NG-002`; an ID is never reused for a different rule. |
| `name`, `language`, `category` | Human name and applicability. Seed languages are `angular` and `csharp`. |
| `defaultOn` | Whether the rule belongs to the automatically applied core. |
| `statement`, `rationale` | Normative requirement and the reason it matters. |
| `badExample`, `goodExample` | Described counterexample and preferred example. |
| `severity` | Default finding severity: `critical`, `high`, `medium`, `low`, or `info`. |
| `autofixable` | Whether Quality Studio may offer a deterministic fix. It does not authorize an agent to mutate code. |
| `deterministic.check` | Optional pre-check implementation key. Its absence means review-only. |
| `history[]` | In-document semantic version, ISO date, and change description. |

Rules and examples are English. The JSON file is the canonical document; generated catalogues or UI projections must not become a second source of truth.

## Seed sets and grounding

The seed rules use patterns present in the Quality Studio repository and its documented Agent Studio kinship. Angular examples use the shared `--studio-*` token vocabulary, shared workbench primitives in `frontend/src/styles.css`, standalone focused components, signals, and `ChangeDetectionStrategy.OnPush`. The .NET examples use the repository's explicit API records, ASP.NET composition root, cancellation-aware review/sensor/persistence flows, and behavior-named xUnit tests. The Agent Studio handover boundary is deliberately modeled through explicit contracts rather than leaked implementation types.

| ID | Category | Core | Enforcement |
| --- | --- | --- | --- |
| `QS-NG-001` | Focused component structure | No | Agent review |
| `QS-NG-002` | Shared design-token usage | Yes | Agent review + deterministic candidate detection |
| `QS-NG-003` | Standard-component reuse | Yes | Agent review |
| `QS-NG-004` | Template hygiene | Yes | Agent review |
| `QS-NG-005` | OnPush change detection | Yes | Agent review + deterministic pre-check |
| `QS-CS-001` | Explicit API shape | Yes | Agent review |
| `QS-CS-002` | Dependency-injection patterns | Yes | Agent review |
| `QS-CS-003` | Async and cancellation hygiene | Yes | Agent review + deterministic pre-check |
| `QS-CS-004` | Behavior-focused test structure | Yes | Agent review |

`QS-NG-002` and `QS-NG-003` explicitly cover the operator-observed defect class: ad-hoc styles in place of design tokens and one-off UI primitives in place of standard component reuse.

## Project configuration and scope

Project overrides live at exactly `.quality/rules.json` in the reviewed repository and validate against [`quality-rules-config.v1.schema.json`](../schemas/quality-rules-config.v1.schema.json). This file is versioned with the project. There is no central per-project rule settings store.

The file may be omitted when the default-on core is sufficient. Overrides are evaluated in array order; later matching entries refine earlier ones. An entry can change `enabled`, `severity`, `autofixable`, and rule-specific `parameters`.

```json
{
  "$schema": "https://quality.studio/schemas/quality-rules-config.v1.schema.json",
  "schemaVersion": 1,
  "overrides": [
    { "ruleId": "QS-NG-001", "enabled": true },
    { "ruleId": "QS-NG-002", "severity": "high" },
    {
      "ruleId": "QS-NG-002",
      "enabled": false,
      "scope": { "include": ["legacy/*"], "exclude": ["legacy/migrated/*"] }
    }
  ]
}
```

Scope is the intersection of its selectors:

- `languages` restricts the override to `angular` and/or `csharp` subjects.
- `include` requires at least one repository-relative simple glob to match.
- `exclude` suppresses the override when any repository-relative simple glob matches.
- A missing `scope` is repository-wide. Paths use `/` and are matched case-sensitively for portable repository behavior.
- An override naming an unknown rule is an error; misspelled IDs never silently weaken review policy.

For aggregate reviews, a rule applies when it applies to at least one descendant subject file. The effective set and adjusted values are hashed into the review-input identity, so a rule or override change produces visible policy drift even when source code is unchanged.

## Review and deterministic integration

The resolver selects language rules, applies repository overrides, and adds the effective named documents to every review prompt. The prompt requires the reviewer to use the exact stable ID. Active rules are persisted in `reviewInputs.standards` with `scope: "built-in"`, their document version, and content hash. An agent response that claims a `QS-*` rule inactive for that subject is rejected.

The `quality-rules` deterministic sensor is part of the static-analysis wave. It enforces only checks with low interpretation risk:

- raw Angular color and pixel candidates outside custom-token declarations (`QS-NG-002`);
- Angular components missing an OnPush declaration (`QS-NG-005`);
- C# `async void` and sync-over-async constructs (`QS-CS-003`).

These results keep `source.kind: "deterministic"`, sensor provenance, and the named rule ID. They remain separate from agent-authored findings and do not set the review grade. Component reuse, API boundaries, DI lifetime correctness, and test quality require context and therefore stay agent-reviewed.

## Living-document versioning

Rule IDs are permanent. Clarifications and example improvements increment the rule's latest `history[].version`; behavior or default/severity changes require at least a minor increment; incompatible meaning requires a new rule ID and deprecation of the old document. History entries are append-only and repository history supplies the complete diff. Schema evolution uses a new schema file and `schemaVersion`.

Changing a rule does not rewrite old review metadata. A subsequent review records the new version and effective hash, preserving which policy produced each historical finding.

## Unresolved security findings and push policy

An unfixed security finding remains visible in review metadata, reports, and finding state. Withholding a push while also leaving the finding unfixed is itself a problem: it can hide the issue on a private worktree without reducing the underlying risk or creating shared evidence.

The current policy is documentation and visibility only. Quality Studio takes **no push-blocking enforcement action** for this edge case. The finding stays recorded and visible until resolved, waived with rationale, or superseded by a later review. This avoids conflating source-control delivery mechanics with remediation while the product lacks a complete, auditable exception and ownership workflow. Push gating may be reconsidered only as a separate policy decision.
