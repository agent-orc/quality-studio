# Named rule library dossier

Status: v1, adopted 2026-08-12. Quality Studio owns this language-specific library. It is distinct from project-authored review inputs: a rule is a versioned statement of what good code means, while an input can supply temporary or project-specific context.

## File-first contract

The canonical library is the English Markdown under `rules/<language>/`. The core package embeds those exact files for standalone use; there is no database copy. One file defines one rule and its stable id. Renames may move a file but must not change its id. If a rule's meaning changes incompatibly, retire it and allocate a new id instead of reusing the old identity.

Each file has strict frontmatter:

```yaml
---
id: QS-NG-002
version: 1.0.0
title: Use design tokens instead of ad-hoc visual literals
language: angular
kinds: [code]
appliesTo: [**/*.css, **/*.scss, **/*.html]
severity: high
defaultOn: true
autofixable: false
deterministic: true
---
```

It then has these required, non-empty level-two sections:

- `Statement`: the normative instruction.
- `Rationale`: why the instruction improves this codebase.
- `Bad example`: a fenced, language-appropriate counterexample.
- `Good example`: a fenced, language-appropriate preferred example.
- `Change history`: dated entries describing rule revisions.

`severity` is one of `critical`, `high`, `medium`, `low`, or `info`. `autofixable` means Quality Studio has a safe semantic fix; it is not a prediction that an agent could edit the code. `deterministic` means at least part of the rule has a mechanical pre-check. A deterministic rule may still need agent judgment for cases the pre-check cannot prove.

Versions follow semantic versioning. Editorial clarifications increment patch, materially broader or narrower interpretation increments minor, and incompatible meaning requires a new rule id. Every change adds a dated history entry. Findings retain the stable rule id; review metadata records the effective rule version and content hash, so a rule edit produces visible policy drift.

## Core defaults and repository overrides

The defined default-on core is:

| Rule | Default | Deterministic subset |
| --- | --- | --- |
| `QS-NG-002` design tokens | on | raw visual literals in feature CSS/SCSS |
| `QS-NG-003` standard component reuse | on | no; requires repository/component context |
| `QS-NG-004` template hygiene | on | inline style attributes and bindings |
| `QS-NG-005` OnPush change detection | on | no; requires component-role judgment |
| `QS-DN-001` API contracts | on | no |
| `QS-DN-002` dependency injection | on | no |
| `QS-DN-003` async hygiene | on | blocking task consumption |
| `QS-DN-004` test structure | on | no |

`QS-NG-001` is opt-in because component boundaries vary more by repository. An absent configuration file does not disable the core: applicable default-on rules still apply to every project.

Project configuration is the versioned file `.quality/rules.json`, validated by `schemas/rule-config.v1.schema.json`. Quality Studio does not store per-project rule state centrally. The complete shape is:

```json
{
  "$schema": "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json",
  "version": 1,
  "rules": {
    "QS-NG-005": { "enabled": false },
    "QS-DN-003": { "severity": "critical" }
  },
  "scopes": [
    {
      "paths": ["legacy/**"],
      "rules": {
        "QS-NG-002": {
          "severity": "low",
          "options": { "allowedPixelValues": ["0", "1", "2"] }
        }
      }
    }
  ]
}
```

Root `rules` overrides are repository-wide. `scopes` use the same case-sensitive Git-wildmatch path semantics as `.quality/scope.json`. Root overrides apply first; matching scopes apply in array order, and a later matching scope wins for properties it supplies. An override may change `enabled`, `severity`, and `options`; omitted properties inherit. Options merge by key. Unknown rule ids, contract properties, severities, parent traversal, and empty overrides make configuration unavailable rather than silently weakening policy.

The v1 deterministic options are:

- `QS-NG-002`: `allowedPixelValues` (string array, default `0` and `1`) and `ignorePaths` (glob array, intended for central token-definition files).
- `QS-NG-004`: `allowedBindings` (exact matched strings).
- `QS-DN-003`: `allowBlockingCalls` (boolean; use only for a tightly path-scoped compatibility boundary).

## Seed sets

The Angular seed is grounded in Agent Studio's standalone signal components, feature barrels, folder-per-component layout, external templates/styles, `--studio-spacing-*` scale, and shared `app-tree-row`/`app-count-badge`/`cac-chat` surfaces, together with Quality Studio's actual OnPush explorer, central `--studio-*` tokens, and pane/status/control primitives:

- `QS-NG-001`: focused, colocated component structure.
- `QS-NG-002`: design-token use. This names the operator-observed defect “ad-hoc styles instead of design tokens.”
- `QS-NG-003`: reuse of standard components. This names the operator-observed defect “no component reuse.”
- `QS-NG-004`: template hygiene.
- `QS-NG-005`: OnPush/reactive change detection.

The C#/.NET seed is grounded in Quality Studio's minimal API contracts, constructor-injected services and explicit lifetimes, concurrent review/sensor paths, cancellation propagation, disposable repository fixtures, and contract-level xUnit assertions:

- `QS-DN-001`: explicit API shape and boundary validation.
- `QS-DN-002`: constructor DI and intentional lifetimes.
- `QS-DN-003`: non-blocking, cancellation-aware async code.
- `QS-DN-004`: behavior-oriented test structure.

## Review and static-analysis integration

For every subject, Quality Studio selects rules by review kind and `appliesTo`, then applies `.quality/rules.json`. Enabled rules enter the prompt under “Named Quality Studio rules” with stable id, version, effective severity, autofixability, statement, rationale, examples, and options. They are included before free-form global/project inputs in the review-input budget. Every agent finding must reference the exact rule id; `built-in:<kind>` remains available only for base review criteria that have no named rule.

The effective rule text participates in the review-input hash and is recorded in `reviewInputs.standards`. Rule changes and project overrides therefore make previous metadata `policyDrift`, not silently current.

`quality rules check [path]` runs the static-analysis wave. The API also registers `qs-rules` as a default deterministic evidence sensor. Its findings use the same `QS-*` ids and deterministic provenance, remain separate from agent-authored findings, and are supplied to the review prompt as prior facts. V1 mechanically checks `QS-NG-002`, `QS-NG-004`, and the blocking-call subset of `QS-DN-003`; other rules remain agent-reviewed until a sound deterministic check exists.

## Unfixed security findings and push policy

Security findings that remain unfixed stay visible and recorded in review truth, including their state, evidence, and history. Withholding a push while also leaving the finding unfixed is itself an undesirable limbo: it can hide the change from normal collaboration without reducing the underlying risk.

For now Quality Studio takes **no enforcement action and does not block pushes** for this edge case. The rationale is that a push gate cannot by itself resolve the vulnerability and can encourage work to remain only on an operator machine. Visibility, durable recording, and explicit disposition are the current controls. A later policy may add enforcement only with a defined remediation/exception workflow and a safe way to publish the finding without publishing the vulnerable change.
