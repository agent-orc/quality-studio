# Schemas

JSON Schema draft 2020-12 artifacts for every document Quality Studio writes, plus
the rule pool contracts a reviewed repository or host writes for Quality Studio to
read: `rule-config.v1` (overrides), `rule-applicability.v1` (which packs apply),
`rule-pack.v1` (custom packs) and `rule-set.v1` (one-file import and export).

[`change-review-verdict.v1.schema.json`](change-review-verdict.v1.schema.json)
describes the shadow code-quality verdict returned by `POST /api/repos/{id}/change-review`.
It binds the exact Git range, effective rule set and project policy and requires
rule-cited findings on changed lines. See [`../docs/change-reviews.md`](../docs/change-reviews.md).
[`change-review-policy.v1.schema.json`](change-review-policy.v1.schema.json) describes
the repository-owned `.quality/policy.json` thresholds.

## Canonical domain

Every `$id` uses `https://agent-orchestrator.dev/quality/schemas/<name>.schema.json`.
That is the product URL in the repository README, and it is the only form current
writers emit.

Until 2026-09-06 the newer schemas here and the C# constants that write their
documents used `https://quality.studio/schemas/<name>.schema.json`. Both URLs
identify the same document. The old form is an accepted alias: those schemas list
both values for a document's own `$schema` property, and the two contracts that
reject a mismatched schema id — the quality finding envelope and change-review
evidence — accept it explicitly. Artifacts already committed under `.quality/`
keep the value their producing run wrote; they are not rewritten.

The schemas are not published under that URL today. They live only in this
repository, and the copy here is authoritative.

## Versions

`review-meta` is the only document with more than one live version. Writers emit
v3; readers accept v1, v2, and v3. The version table, the per-field semantics, and
the compatibility rule are in
[`../docs/concept.md`](../docs/concept.md#review-meta-schema-v3).

On 2026-09-06 the `standards[].id` and `reviewInputs.omitted` patterns in all three
`review-meta` versions widened from `^[a-z0-9][a-z0-9._-]{1,127}$` to accept upper-case
letters, so a named rule can be recorded under the id it is cited by (`QS-CS-003`). The
change is permissive in one direction only: every document written under the earlier
pattern is still valid, so it is an edit within each version rather than a new one.

`quality-report.v1` and `quality-run-report.v1` carry `aggregateScore` and
`aggregateBand`. The older `score` and `grade` properties hold the same values and
are marked deprecated; see [`../docs/quality-reports.md`](../docs/quality-reports.md).

## Review response

[`review-response.v1.schema.json`](review-response.v1.schema.json) is the one JSON object a file,
module or project review agent returns. It is embedded into the analysis library and appended to
every review prompt; the response parser enforces the same shape and additionally checks aspect
references and canonicalizes rule and aspect ids. See
[`../docs/review-runs.md`](../docs/review-runs.md#answers-the-parser-refuses).

## Quality domains reference

[`quality-domains.v1.schema.json`](quality-domains.v1.schema.json) describes the
central bilingual [`../rules/quality-domains.json`](../rules/quality-domains.json)
reference catalogue. It distinguishes existing metrics, review rules and planned
checks without duplicating rule content or formulas. Property selectors are
documentation only: missing properties remain unknown and never inherit from a
project into its components. `npm run domains:check` also verifies cross-catalogue
IDs and generated Angular data; it is included in `npm run rules:check`.

## Rule pool contracts

The rule pool is described in [`../docs/rule-pool-management.md`](../docs/rule-pool-management.md).

| Schema | Document |
| --- | --- |
| `rule-catalogue.v1` | The generated built-in library. `$defs/entry` is also a custom rule's shape; the built-in `QS-` prefix is enforced by the generator per directory, so the entry's id pattern admits a custom owner prefix. |
| `rule-config.v1` | `overrides.json` in a project or the global rule folder, and the read-only shared `rule-overrides.json`. The id pattern admits custom rule ids. |
| `rule-applicability.v1` | `applicability.json`: the packs that replace the house-style default. |
| `rule-pack.v1` | A pack: built-in in `rules/packs/`, custom in `packs/<id>.json`. |
| `rule-pack-catalogue.v1` | The generated `rule-packs.v1.json` embedded next to the rule catalogue. |
| `rule-set.v1` | One scope's applicability, overrides, custom rules and packs, as exported and imported. It references the three schemas above by relative `$ref`. |
| `rule-audit.v1` | One line of a rule pool `audit.jsonl`. |

On 2026-09-28 the id patterns of `rule-catalogue.v1` and `rule-config.v1` widened from
`^QS-[A-Z]{2,4}-[0-9]{3}$` to `^[A-Z][A-Z0-9]{1,7}-[A-Z]{2,4}-[0-9]{3}$` so custom rules use
the same contracts. Every earlier document is still valid, so this is an edit within v1.
