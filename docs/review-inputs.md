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

Every generated finding requires a `ruleId`. Supplied guidelines use their
frontmatter `id`; base prompt rules use `built-in:<kind>`. The UI uses that stable
identity to show findings per guideline and the producing guideline on each finding.

The effective input hash covers the versioned prompt template and only the guideline
content actually included after precedence and budgeting. It never includes source
code. A current code manifest with a different effective input hash is reported as
`policyDrift` (shown as “Guideline changed”); a different code manifest remains
`stale` (shown as “Code changed”).

The guideline editor can dry-run an unsaved draft against one to ten sample files.
It runs both current and draft policy through the reviewer, compares stable finding
identities, and reports added and removed findings without writing review metadata.
