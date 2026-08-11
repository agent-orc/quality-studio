# Deterministic analyzer evidence

Quality Studio treats analyzer output as prior machine evidence, not as an agent opinion. The review sidecar stores it in `deterministicEvidence`, separately from the agent-authored `findings` array. Every analyzer finding has:

- `source.kind: "deterministic"`;
- the Quality Studio sensor id and the SARIF producer name/version;
- the producer's analyzer identifier in `ruleId`.

The prompt asks the agent to judge applicability, deduplicate and prioritise these facts instead of repeating them. The stored `grade`, rationale and aspects remain the agent's statement; analyzer evidence does not change or cap the score. The review UI and generated quality report label analyzer findings with their deterministic source.

## Host-owned analyzer profiles

Repositories cannot provide analyzer commands. The API host defines immutable analyzer
profiles under `QualityStudio:AnalyzerProfiles`; a repository registration may select
only a profile id whose `SensorId` matches the sensor. Command-backed sensors (`sarif`,
`roslyn`, `eslint`, and `tsc`) are disabled by default and cannot be enabled without a
configured profile. Requests containing a free-form `configuration.command` are
rejected.

Profiles keep the executable and argument template in operator-controlled deployment
configuration. Commands are launched directly, without a shell. The placeholders
`{repositoryRoot}`, `{target}` and `{reportPath}` are expanded inside individual
arguments. `ReportPath` and an optional `WorkingDirectory` must remain inside the
repository. For example:

```json
{
  "QualityStudio": {
    "AnalyzerProfiles": {
      "roslyn-standard": {
        "SensorId": "roslyn",
        "Executable": "dotnet",
        "Arguments": [
          "build",
          "QualityStudio.slnx",
          "--no-restore",
          "-p:ErrorLog={reportPath};version=2.1"
        ],
        "ReportPath": ".quality/analyzers/roslyn.sarif"
      }
    }
  }
}
```

The repository registration selects that profile without receiving command authority:

```json
{
  "id": "roslyn",
  "enabled": true,
  "profileId": "roslyn-standard"
}
```

### Generic SARIF 2.1.0

The `sarif` sensor accepts a report from any producer. Its host profile names the
approved producer executable and report path.

```json
{
  "SensorId": "sarif",
  "Executable": "custom-analyzer",
  "Arguments": ["--sarif", "{reportPath}"],
  "ReportPath": ".quality/analyzers/custom.sarif"
}
```

The importer maps every run independently, resolves driver and extension rule metadata, and includes primary, related, code-flow and stack locations. A producer fingerprint is used when present.

### Roslyn analyzers from `dotnet build`

```json
{
  "SensorId": "roslyn",
  "Executable": "dotnet",
  "Arguments": ["build", "QualityStudio.slnx", "--no-restore", "-p:ErrorLog={reportPath};version=2.1"],
  "ReportPath": ".quality/analyzers/roslyn.sarif"
}
```

The configured command decides which project, target frameworks and analyzer set are built. A non-zero build exit is accepted when it still produced a readable SARIF report, because analyzer diagnostics commonly accompany a failed build.

### ESLint

ESLint needs a SARIF formatter already installed in that repository:

```json
{
  "SensorId": "eslint",
  "Executable": "npx",
  "Arguments": ["--no-install", "eslint", ".", "--format", "@microsoft/eslint-formatter-sarif", "--output-file", "{reportPath}"],
  "ReportPath": ".quality/analyzers/eslint.sarif",
  "WorkingDirectory": "frontend"
}
```

`--no-install` prevents a review from downloading tools as a side effect.

### TypeScript `tsc --noEmit`

TypeScript does not emit SARIF itself. The `tsc` sensor captures non-pretty compiler output, stores it at the configured report path and maps `TS####` diagnostics to the same deterministic finding contract.

```json
{
  "SensorId": "tsc",
  "Executable": "npx",
  "Arguments": ["--no-install", "tsc", "--noEmit", "--pretty", "false"],
  "ReportPath": ".quality/analyzers/tsc.txt",
  "WorkingDirectory": "frontend",
  "ProducerVersion": "5.9.2"
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
