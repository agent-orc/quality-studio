# Deterministic analyzer evidence

Quality Studio treats analyzer output as prior machine evidence, not as an agent opinion. The review sidecar stores it in `deterministicEvidence`, separately from the agent-authored `findings` array. Every analyzer finding has:

- `source.kind: "deterministic"`;
- the Quality Studio sensor id and the SARIF producer name/version;
- the producer's analyzer identifier in `ruleId`.

The prompt asks the agent to judge applicability, deduplicate and prioritise these facts instead of repeating them. The stored `grade`, rationale and aspects remain the agent's statement; analyzer evidence does not change or cap the score. The review UI and generated quality report label analyzer findings with their deterministic source.

## Repository configuration

Analyzer commands are repository-specific entries in the existing `sensors` array. Commands are launched directly, without a shell. The placeholders `{repositoryRoot}`, `{target}` and `{reportPath}` are expanded inside individual arguments. `reportPath` and an optional `workingDirectory` must remain inside the repository.

### Generic SARIF 2.1.0

The `sarif` sensor accepts a report from any producer. `command` is optional when another process has already created the report.

```json
{
  "id": "sarif",
  "enabled": true,
  "configuration": {
    "command": "custom-analyzer --sarif {reportPath}",
    "reportPath": ".quality/analyzers/custom.sarif"
  }
}
```

The importer maps every run independently, resolves driver and extension rule metadata, and includes primary, related, code-flow and stack locations. A producer fingerprint is used when present.

### Roslyn analyzers from `dotnet build`

```json
{
  "id": "roslyn",
  "enabled": true,
  "configuration": {
    "command": "dotnet build QualityStudio.slnx --no-restore -p:ErrorLog={reportPath};version=2.1",
    "reportPath": ".quality/analyzers/roslyn.sarif"
  }
}
```

The configured command decides which project, target frameworks and analyzer set are built. A non-zero build exit is accepted when it still produced a readable SARIF report, because analyzer diagnostics commonly accompany a failed build.

### ESLint

ESLint needs a SARIF formatter already installed in that repository:

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

`--no-install` prevents a review from downloading tools as a side effect.

### TypeScript `tsc --noEmit`

TypeScript does not emit SARIF itself. The `tsc` sensor captures non-pretty compiler output, stores it at the configured report path and maps `TS####` diagnostics to the same deterministic finding contract.

```json
{
  "id": "tsc",
  "enabled": true,
  "configuration": {
    "command": "npx --no-install tsc --noEmit --pretty false",
    "reportPath": ".quality/analyzers/tsc.txt",
    "workingDirectory": "frontend",
    "producerVersion": "5.9.2"
  }
}
```

## Unavailable analyzers

Unavailable is evidence, not a clean scan. The sensor returns `available: false`, an empty finding list and a visible `unavailableReason` when:

- command or report-path configuration is missing;
- the executable or ESLint formatter is not installed;
- the command produces no report;
- the report is not valid SARIF 2.1.0;
- `tsc` exits unsuccessfully without a parseable diagnostic.

Before a configured SARIF command runs, Quality Studio removes its old report. A failed analyzer therefore cannot make stale output look current.
