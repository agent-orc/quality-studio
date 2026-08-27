# Quality Studio named rule library

This directory is the source-of-truth for Quality Studio's own named coding
rules — the "this is good code" library referenced by review findings'
`ruleId`. See [`docs/rules.md`](../docs/rules.md) for the full format spec,
the default-on/override contract, and the review-integration story; this
file is just the directory map.

```
rules/
  angular/   QS-NG-* — Angular / TypeScript rules
  dotnet/    QS-CS-* — C# / .NET rules
```

Each rule is one Markdown file with YAML frontmatter (id, title, summary,
technology, category, severity, autofixable, defaultOn, kinds, levels,
version, status, optional deterministicCheck) followed by a body: statement,
rationale, a good example, a bad example, and a changelog. `AgentOrchestrator.CodeQuality`
embeds every file under this tree at build time (see
`src/AgentOrchestrator.CodeQuality/AgentOrchestrator.CodeQuality.csproj`) so
the built-in rules are available when reviewing any target repository, not
just this one.

## Adding a rule

1. Pick the next free id in the technology's sequence (`QS-NG-00N` or
   `QS-CS-00N`); ids are stable once shipped and are never reused.
2. Copy the frontmatter shape from an existing rule and validate it against
   [`schemas/rule.v1.schema.json`](../schemas/rule.v1.schema.json).
3. Ground the good/bad examples in real code — cite an actual file and line,
   not a hypothetical, whenever the codebases here already show the pattern.
4. Add a `## Changelog` entry with today's date.

## Current seed set

| Angular | Category | Severity | Default-on |
| --- | --- | --- | --- |
| QS-NG-001 | design-tokens | high | yes |
| QS-NG-002 | standard-component-reuse | medium | yes |
| QS-NG-003 | component-structure | medium | no |
| QS-NG-004 | template-hygiene | medium | no |
| QS-NG-005 | change-detection | high | yes |

| .NET | Category | Severity | Default-on |
| --- | --- | --- | --- |
| QS-CS-001 | api-shape | medium | no |
| QS-CS-002 | dependency-injection | medium | no |
| QS-CS-003 | async-hygiene | high | yes |
| QS-CS-004 | test-structure | low | no |
