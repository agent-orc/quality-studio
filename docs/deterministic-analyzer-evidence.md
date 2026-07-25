# Deterministic analyzer evidence

Quality Studio treats analyzer output as prior evidence, not as an agent opinion. Analyzer findings carry a `source` object whose `kind` is `deterministic`, preserve the sensor and producer, and retain the producer's rule identifier as `ruleId`. Agent findings remain in `findings`; machine output is stored separately in `deterministicEvidence`. The grade is always the agent's explicit statement.

Before an API review starts, each enabled analyzer evidence sensor runs once for the repository. Its available or unavailable result is included in every review prompt, with findings filtered to the review subject. The prompt tells the agent to judge, deduplicate, and prioritise the evidence instead of repeating it.

Analyzer sensors are disabled by default. Configure them on each repository through its `sensors` registration. Commands are parsed as an executable plus arguments and are not run through a shell. These placeholders are available:

- `{reportPath}`: absolute configured report path.
- `{repositoryRoot}`: absolute repository root.
- `{target}`: sensor target or configured working directory.

All configured paths must stay inside the repository.
When `command` is configured, its old generated report is removed before launch
so a failed analyzer cannot make stale output look current.

## SARIF 2.1.0

The `sarif` sensor reads SARIF 2.1.0 from any producer. `reportPath` is required; `command` is optional when another process has already created the report.

```json
{
  "id": "sarif",
  "enabled": true,
  "configuration": {
    "command": "custom-analyzer --sarif {reportPath}",
    "reportPath": ".quality/analyzers/custom.sarif",
    "workingDirectory": "."
  }
}
```

Every SARIF run retains `tool.driver.name`, its reported version, and its zero-based run index in the finding source. Rules are resolved by id or index, including extension tool components. Short and full descriptions, help, help URI, default severity, properties, and message strings are mapped without inventing missing metadata. Physical locations from primary and related locations, code-flow thread locations, and stack frames are retained. Artifact indexes, URI base ids, regions, and logical symbols are supported.

`error`, `warning`, and `note` map to Quality Studio `high`, `medium`, and `low`; missing or `none` levels map to `info`.

## Roslyn from `dotnet build`

Roslyn/MSBuild can write SARIF directly:

```json
{
  "id": "roslyn",
  "enabled": true,
  "configuration": {
    "command": "dotnet build --no-restore --nologo -p:ErrorLog={reportPath};version=2.1",
    "reportPath": ".quality/analyzers/roslyn.sarif",
    "workingDirectory": "."
  }
}
```

The configured build decides which solution/project, configuration, target framework, and analyzers apply. A non-zero build exit is accepted only when it produced a readable SARIF report; the diagnostics remain evidence rather than being mistaken for sensor failure.

## ESLint

Configure ESLint with a SARIF formatter installed in the repository:

```json
{
  "id": "eslint",
  "enabled": true,
  "configuration": {
    "command": "npx --no-install eslint . --format @microsoft/eslint-formatter-sarif --output-file {reportPath}",
    "reportPath": ".quality/analyzers/eslint.sarif",
    "workingDirectory": "frontend"
  }
}
```

`--no-install` prevents a scan from silently downloading a missing analyzer or formatter.

## TypeScript `tsc --noEmit`

TypeScript does not natively emit SARIF. The `tsc` sensor captures stable, non-pretty compiler diagnostics at the configured report path and maps them to the same finding contract:

```json
{
  "id": "tsc",
  "enabled": true,
  "configuration": {
    "command": "npx --no-install tsc --noEmit --pretty false",
    "reportPath": ".quality/analyzers/tsc.txt",
    "workingDirectory": "frontend"
  }
}
```

For `path(line,column): error TS2322: message`, the source is deterministic `TypeScript`, the rule id is `TS2322`, and duplicate compiler lines become one finding.

## Unavailable behavior

Unavailable is explicit and is never interpreted as clean:

- a configured executable cannot be launched;
- an analyzer or formatter is not installed;
- `reportPath` or `command` is missing where required;
- a command does not produce its configured SARIF report;
- the report is missing, unreadable, invalid JSON, or not SARIF 2.1.0;
- `tsc` exits unsuccessfully without any parseable compiler diagnostic.

The scan response has `available: false`, a concrete `unavailableReason`, no findings, and sensor provenance. Review prompts preserve that unavailable state as unknown evidence.
