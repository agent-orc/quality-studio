# Review inputs

Quality Studio reads project review guidance from `.quality/inputs/*.md` in the reviewed repository. A separate global directory can be configured with `QualityStudio:GlobalInputsDirectory`, the `QUALITY_GLOBAL_INPUTS` environment variable, or the CLI's `--global-inputs` option.

Each Markdown file starts with small frontmatter:

```markdown
---
id: dotnet-style
enabled: true
kinds: [code, performance]
levels: [file]
priority: 100
---
Prefer cancellation-aware asynchronous APIs on request paths.
```

`kinds` and `levels` accept comma-separated bracket lists; `all` applies everywhere. Singular `kind` and `level` are also accepted. Higher priority inputs are injected first. Applicable global inputs precede project inputs, while a project input with the same `id` replaces its global counterpart.

`enabled` defaults to `true`. The Guidelines workspace in Quality Studio creates,
edits, enables/disables, and deletes these files directly. Changes are ordinary
repository working-tree changes: Quality Studio does not hide them in application
state or commit them automatically. The starter catalogue contains .NET, Angular /
TypeScript, testing, and security entries; installing one copies it into
`.quality/inputs` so it can be edited like any other guideline.

The default 12,000-character budget is configurable as `QualityStudio:InputBudgetCharacters` or with `--input-budget`. Partial and omitted content is reported by the resolver and persisted in `reviewInputs.omitted`; it is never silently dropped.

Use `quality review <file> --kind code --explain-inputs` to inspect the exact selection without running an agent review.

## Rule library

Alongside the repository's own guidelines, every review resolves the built-in named-rule library
in [`rules/`](../rules/README.md). Its rules are authored as Markdown, generated into
`backend/AgentOrchestrator.CodeQuality/catalogues/rule-catalogue.v1.json` by
`npm run rules:sync`, and embedded in the analysis-core assembly, so they need no per-repository
install step and no network access.

A rule reaches a review when its `kinds` contain the review kind and its `technology` matches the
reviewed unit's hierarchy adapter — `dotnet`, `angular`, or `generic`, which matches every
adapter. The resolver renders each applicable rule as a review input with scope `built-in`, id
equal to the rule id (`QS-CS-003`), and a priority derived from its effective severity. Built-in
rules travel with the global guidelines into the prompt, compete with them on priority, and are
subject to the same character budget; project inputs stay last and still win by id, over a rule as
over a global file. The prompt receives the rule's statement and detection guidance; its rationale
and examples stay in the catalogue and on `GET /api/rules`.

A repository adjusts the library in `.quality/rules/overrides.json`
([`schemas/rule-config.v1.schema.json`](../schemas/rule-config.v1.schema.json)), with an optional
shared `rule-overrides.json` in the global inputs directory. An entry names a known rule id, sets
`enabled`, `severity`, or both, and states a reason; the reason is carried into the prompt when a
severity is changed, so the agent knows the repository decided this deliberately. Everything works
with neither file present.

Each rule contributes to `reviewInputs.standards[]` with its own version, and to the effective
input hash. Enabling, disabling, or editing a rule therefore shows the affected units as
`policyDrift`, exactly as editing a guideline does. `GET /api/rules` and
`GET /api/repos/{repoId}/rules` return the resolved catalogue for a repository with a trace per
rule — its source scope, whether it is enabled, whether its severity was overridden, and the
kinds and adapters it reaches. Both accept optional `kind` and `adapter` query parameters.

Every generated finding requires a `ruleId`. Supplied guidelines use their
frontmatter `id`; named rules use their rule id; base prompt rules use `built-in:<kind>`. The
parser accepts only ids the resolved inputs carry and replaces anything else with
`built-in:<kind>`, logged as `RuleIdRejected`. The UI uses that stable
identity to show findings per guideline and the producing guideline on each finding.

The effective input hash covers the versioned prompt template and only the guideline
content actually included after precedence and budgeting. It never includes source
code. A current code manifest with a different effective input hash is reported as
`policyDrift` (shown as “Guideline changed”); a different code manifest remains
`stale` (shown as “Code changed”).

The guideline editor can dry-run an unsaved draft against one to ten sample files.
It runs both current and draft policy through the reviewer, compares stable finding
identities, and reports added and removed findings without writing review metadata.
