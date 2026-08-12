# Named best-practice rule library

Quality Studio owns a versioned, English rule catalogue for language-specific
code review. The source of truth is the repository's [`rules/`](../rules/README.md)
tree. The core package embeds those files at build time, so the same catalogue
is available when Quality Studio reviews another registered repository.

This document is the format and seed-set dossier for catalogue version 1.

## Rule document format

One Markdown file defines one rule. The file name is the stable rule id and the
directory identifies the language family. Required YAML frontmatter fields are:

| Field | Contract |
|---|---|
| `id` | Permanent `QS-<LANGUAGE>-<three digits>` identity. The current namespaces are `QS-NG` and `QS-CS`. |
| `title` | Short, specific rule name. |
| `language` | Language family used for catalogue grouping. |
| `severity` | `critical`, `high`, `medium`, `low`, or `info`. |
| `autofixable` | Explicit `true` or `false`. It describes whether the entire violation can be corrected without semantic judgement. |
| `version` | Three-part semantic version. |
| `status` | `active` or `deprecated`. |
| `kinds` | Review kinds to which the rule applies. |
| `levels` | Review levels to which the rule applies. |
| `applies-to` | File extensions that select the rule. |
| `references` | Code or instruction sources that grounded the rule. |
| `deterministic-check` | Sensor check id for an enforced subset, or `none`. |

Every file must contain these non-empty level-two sections in this order:

1. `Statement`
2. `Rationale`
3. `Bad example`
4. `Good example`
5. `Change history`

The parser rejects missing fields, invalid ids, invalid versions, unsupported
scope values, missing examples, and duplicate ids. `rules/README.md` is the only
non-rule Markdown file ignored by the loader.

## Resolution and prompt contract

For each review operation, Quality Studio selects active rules by review kind,
review level, and the extensions of all subject files. Applicable rules enter
the existing review-input budget before global and project guidelines. They are
stored in `reviewInputs.standards` with scope `built-in`, their semantic version,
and a content hash. As a result, a normative rule change produces visible policy
drift independently from source-code staleness.

The prompt presents them in a separate `Quality Studio named rules` section.
When an agent reports a named-rule violation, the finding must use that exact
`QS-*` id. Findings remain location-bound and receive the existing verified
fingerprint and lifecycle handling.

Repository guidelines continue to serve local or project-specific policy. They
do not rename, override, or recycle Quality Studio rule ids.

## Deterministic pre-check contract

The `quality-rules` sensor runs in the API-owned static-analysis wave and is also
available through the sensor scan API. Its output stays in
`deterministicEvidence`, separate from agent findings and grades. The first
enforced subset is intentionally narrow:

- `QS-NG-002`: raw pixel and color literals in CSS or SCSS declarations for
  spacing, color, background, and radius properties.
- `QS-NG-004`: inline `template` or `styles` metadata in an Angular component.

The pre-check uses the catalogue rule id as its deterministic `ruleId`. It does
not claim to prove semantic component reuse, correct token choice, template
clarity, or change-detection behavior. Those rules still require review
judgement. `autofixable` is independent from detectability; both initial checks
are detectable but require semantic choice to fix and therefore remain false.

## Angular seed set

| Rule | Area | Enforcement |
|---|---|---|
| `QS-NG-001` | Focused, colocated component structure and public feature boundaries | Review |
| `QS-NG-002` | Central design-token usage instead of ad-hoc style values | Deterministic subset plus review |
| `QS-NG-003` | Standard-component and shared-primitive reuse | Review |
| `QS-NG-004` | External, declarative, typed template hygiene | Deterministic subset plus review |
| `QS-NG-005` | Signal-based, `OnPush`, bounded change detection | Review |

The Angular set is grounded in Quality Studio's `frontend/src/styles.css`,
`frontend/DESIGN-KINSHIP.md`, and signal-based `OnPush` components, plus Agent
Studio's central semantic token scale, folder-per-component rule, feature
barrels, and canonical workbench component contracts.

## C# and .NET seed set

| Rule | Area | Enforcement |
|---|---|---|
| `QS-CS-001` | Explicit, validated, transport-safe API contracts | Review |
| `QS-CS-002` | Constructor injection and truthful service lifetimes | Review |
| `QS-CS-003` | End-to-end asynchronous, cancellation-aware flows | Review |
| `QS-CS-004` | Observable behavior tests with isolated real boundaries | Review |

The .NET set is grounded in Quality Studio's API contracts, composition root,
review orchestration, cancellation flow, and temporary-repository tests, plus
Agent Studio's host registrations, shared request contracts, runner services,
and lifecycle-policy test suites.

## Versioning and change history

Rules are living documents with permanent identities:

- Patch versions clarify wording or examples without changing the obligation.
- Minor versions broaden a rule within its existing intent or enforcement.
- Major versions change the obligation in a compatibility-relevant way.
- Every edit adds a dated entry to `Change history`.
- Deprecated rules remain readable and keep their id, but are no longer resolved
  into new prompts.
- A removed or repurposed id is invalid. A genuinely different obligation gets
  the next unused id in its language namespace.

