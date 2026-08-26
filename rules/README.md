# Quality Studio rule library

Named, versioned best-practice rules, one file per rule, validated against
[`schemas/rule.v1.schema.json`](../schemas/rule.v1.schema.json).

```
rules/
  angular/QS-NG-NNN.json
  dotnet/QS-DN-NNN.json
```

Format, seed-set rationale, review integration, versioning, and the
per-project override file are documented in
[`docs/concepts/rule-library.md`](../docs/concepts/rule-library.md). Don't
edit rule content without reading that first — `defaultOn: true` rules are
resolved automatically into every project's review context unless overridden
in `.quality/rules.config.json`; materializing copies under `.quality/inputs/`
through the sync endpoint is optional
([`schemas/rule-config.v1.schema.json`](../schemas/rule-config.v1.schema.json)).

These files are embedded into the `AgentOrchestrator.CodeQuality` assembly
(see `RuleLibrary.cs` and the `rules/**/*.json` link glob in
`src/AgentOrchestrator.CodeQuality/AgentOrchestrator.CodeQuality.csproj`), the
same way `prompts/` and `catalogues/` already ship with that package.
