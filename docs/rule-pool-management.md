# Rule pool management

The rule pool is every named rule a review can apply to a repository: the built-in library in
[`rules/`](../rules/README.md), plus custom rules a host or a repository adds. This page describes how
the pool is resolved, how an operator manages it in the tool and through the API, and where each
change is stored and recorded.

## Layers

A repository's pool is resolved from four layers, lowest first:

| Layer | Location | Writable through the tool |
| --- | --- | --- |
| Built-in library | embedded `rule-catalogue.v1.json` and `rule-packs.v1.json` | no; changed by a library release |
| Global | `<data root>/rules/` (default `%LOCALAPPDATA%\QualityStudio\rules\`) | yes, by a client allowed to register repositories |
| Shared global inputs | `rule-overrides.json` in a registration's global inputs directory | no; read-only, kept for compatibility |
| Project | `.quality/rules/` in the repository | yes, by a client with access to the repository |

The global and project folders have the same layout:

```
rules/                        or  .quality/rules/
  applicability.json          which packs decide the rule set (rule-applicability.v1)
  overrides.json              per-rule enablement and severity (rule-config.v1)
  custom/<ID>[-slug].md       custom rules in the authored rule format
  packs/<pack-id>.json        custom packs (rule-pack.v1)
```

Project files are author-owned inputs: they are versioned with the code they govern and reviewed like
any other change. The global folder lives in the data root, outside any checkout.

## Resolution

1. **Rules.** The built-in rules, then global custom rules, then project custom rules. An id may be
   defined once; a custom rule cannot redefine a built-in or global rule.
2. **Applicability.** The most specific `applicability.json` wins: project, then global. Without one,
   the built-in `house-style` pack applies, which selects every rule marked `defaultOn`. That is the
   behaviour of every repository before packs existed, so an unconfigured repository reviews exactly
   as it did.
3. **Packs.** A rule is enabled when one of the applicable packs selects it.
4. **Overrides.** Global, then shared global inputs, then project; a later layer replaces an earlier
   one for the same rule id. An override sets `enabled`, `severity`, or both, and always a reason. A
   changed severity and its reason are carried into the prompt.

Once resolved, a rule reaches a review exactly as before: its `kinds` must contain the review kind
and its `technology` must match the unit's adapter. See [review inputs](review-inputs.md#rule-library).

### Packs per project type

A pack is a named selection of rules for one kind of project. Selectors match by `ids`,
`technologies`, `kinds`, `categories` and `defaultOn`; every field that is set must match, a list
matches any of its values, and a pack selects a rule when any selector matches. Selecting by property
rather than by id lets a pack pick up a rule a later library release adds.

| Built-in pack | Selects | Written for |
| --- | --- | --- |
| `house-style` | every `defaultOn` rule | any project; the implicit default |
| `dotnet-service` | default-on .NET and generic rules | ASP.NET Core APIs, workers, CLIs |
| `angular-app` | default-on Angular and generic rules | Angular applications and libraries |
| `public-website` | the opt-in SEO rules | HTML intentionally published for search |
| `security-baseline` | every rule covering the security kind | vendored or integration code |

Packs compose: a public Angular site selects `angular-app` and `public-website`. An empty pack list is
valid and selects nothing, so only rules enabled by an override apply. A pack only decides which rules
apply; a severity change remains an override with its own reason.

### Custom rules

A custom rule is one Markdown file in the same format as the built-in library (frontmatter, the six
required sections, fenced examples, newest-first change history) and becomes a `rule-catalogue.v1`
entry when it is loaded. It is parsed and validated whenever the pool is resolved; no rebuild or
restart is needed. Two constraints differ from built-in rules:

- The id uses the owner's own prefix: `<PREFIX>-<NG|CS|GN>-<NNN>`, for example `ACME-CS-001`. `QS-` is
  reserved for the library so a later release can never collide with a repository's rule. The middle
  segment must match the `technology` (`NG` angular, `CS` dotnet, `GN` generic).
- A file is at most 32 KiB; a scope holds at most 200 custom rules and 50 packs.

### Configuration problems fail closed

An unknown rule or pack id, a missing reason, an invalid custom rule or pack, or a duplicate id makes
the pool invalid. Reviews, the tree and the inputs endpoint then refuse to run (HTTP 422) rather than
review with a rule set the repository did not intend. `GET /api/repos/{repoId}/rules` still answers
and lists every problem under `diagnostics`, located by a scope-relative path, so the problem can be
seen and repaired from the tool.

## Managing the pool in the tool

Open **Review policy → Criteria & metrics → Rules & rationale**.

- **Write changes to** selects the scope: *This repository* (`.quality/rules`) or *All repositories*
  (global). The global option is disabled for credentials that may not register repositories.
- **Packs & applicability** lists every pack with the number of rules it selects in this repository.
  Select packs, give a reason and save; **Remove this scope's choice** returns the scope to the next
  layer (global, then the house style).
- **Custom rules** lists the scope's custom rules. **New custom rule** opens the rule template.
  **Validate** checks the text against the whole pool, including id collisions; only a validated rule
  can be saved. Deleting a custom rule also deletes the same scope's override of it.
- **Import / export** downloads the scope as one rule-set file, or previews and applies one.
- **Audit trail** lists the scope's recorded changes, newest first.
- Every **rule card** has an *Adjust for …* form: enablement (inherit, enabled, disabled), severity
  (inherit or a level) and a reason. The card shows which packs select the rule and where its state
  comes from.

Every write needs a reason. The API validates the change against the complete pool before it touches
a file; a change that would add a configuration problem is rejected with its diagnostics and nothing
is written. A change that repairs an existing problem, or leaves it as it is, is accepted.

## Import and export

A rule set (`rule-set.v1`, [schema](../schemas/rule-set.v1.schema.json)) is one versioned JSON file
holding a scope's applicability, overrides, custom rules (as rule-catalogue entries) and custom packs.
It records the library version it was exported against, the export time, and a `sha256` digest of its
content.

- **Merge** adds or replaces entries by id and keeps the rest; an applicability in the file replaces
  the scope's.
- **Replace** makes the scope hold exactly the file: entries the file lacks are removed, and a file
  without applicability clears the scope's.

A dry run reports every change (`added`, `updated`, `removed`, `unchanged`) and whether the result
would be valid, without writing. An import reports `modifiedSinceExport` when the digest no longer
matches the content; editing an exported file is allowed. Imported custom rules are written back as
Markdown in the authored format, so a repository import produces a reviewable diff. Unknown properties
are rejected rather than silently dropped.

## Audit trail

Each accepted change appends one line (`rule-audit.v1`, [schema](../schemas/rule-audit.v1.schema.json))
with time, actor (the authenticated client id), scope, action, target, reason, and the before and after
state. Custom rules and packs are summarised with a content digest.

| Scope | Audit file |
| --- | --- |
| project | `<project data root>/rules/audit.jsonl` |
| global | `<data root>/rules/audit.jsonl` |

The project audit is generated evidence, so it lives in the data root rather than the checkout. A file
larger than 2 MiB is rotated to `audit-<timestamp>.jsonl` and never truncated. Only changes made
through Quality Studio are recorded; a hand edit of a rule file appears in that file's version history.

## API

All routes exist unscoped (default repository) and under `/api/repos/{repoId}`. `scope` is `project`
(default) or `global`. Writes return the re-resolved pool in the shape of `GET …/rules`; a rejected
change answers 400 with `diagnostics`, a missing entry 404, and a global write by a client that may not
register repositories 403.

| Method and route | Body or query |
| --- | --- |
| `GET /rules` | `kind`, `adapter` filters |
| `PUT /rules/overrides/{ruleId}` | `{ "enabled"?, "severity"?, "reason" }` |
| `DELETE /rules/overrides/{ruleId}` | `reason` |
| `POST /rules/custom/validate` | `{ "content" }`; answers `{ valid, id, rule, diagnostics }` |
| `PUT /rules/custom/{ruleId}` | `{ "content", "reason" }` |
| `DELETE /rules/custom/{ruleId}` | `reason` |
| `PUT /rules/packs/{packId}` | `{ "pack": <rule-pack.v1>, "reason" }` |
| `DELETE /rules/packs/{packId}` | `reason` |
| `PUT /rules/applicability` | `{ "packs": [...], "reason" }` |
| `DELETE /rules/applicability` | `reason` |
| `GET /rules/export` | `name`; answers the rule set as an attachment |
| `POST /rules/import` | `{ "ruleSet", "mode": "merge" \| "replace", "dryRun", "reason" }` |
| `GET /rules/audit` | `limit` (1 to 500, default 100) |

A request body is limited by `QualityStudio:Security:MaxRequestBodyBytes` (64 KiB by default). Raise
it to import a rule set with many custom rules.

## Caching and drift

The resolved pool is cached per repository and keyed by the names, sizes and write times of its
files, so a hand edit is picked up on the next request. The same fingerprint is part of the
hierarchy snapshot key, so a global rule change refreshes the tree and its ETag even though the data
root is outside Git's view. As before, a change to rule text that reaches a prompt shows the affected
units as `policyDrift`.
