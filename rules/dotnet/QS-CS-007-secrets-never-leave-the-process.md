---
id: QS-CS-007
version: 1.0.0
title: Keep secrets and host detail out of logs, findings, and responses
technology: dotnet
kinds: [security]
category: secret-handling
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

A secret a scanner found is never read into a model that can be written, logged, or returned. An
exception message, a filesystem path, and a child process's output stay in the log; a caller gets
a stable title and a status code. Delete a temporary report that held sensitive output in a
`finally`.

## Rationale

`GitleaksSecurityScanner.ParseJsonFinding` reads the rule id, the location, the description, and
the fingerprint and never touches gitleaks' `Secret` or `Match` fields, and `BuildFinding` passes
`null` for the finding's evidence, so the secret is structurally absent from anything Quality
Studio persists — redaction that cannot be forgotten later. The API mirrors it: only
`ReviewModelSelectionException.Message` is echoed to a caller so a path or an internal detail is
never handed back.

## Detection

Look for a scanner's secret-bearing field being mapped into a record, for `exception.Message`,
`exception.ToString()`, a full path, or a child process's stdout or stderr flowing into an HTTP
response or a persisted document, and for a temporary file holding scanner output that is not
deleted on every path. Logging the same detail through the logger is the intended shape.

## Good example

```csharp
// backend/AgentOrchestrator.CodeQuality/GitleaksSecurityScanner.cs
process.StartInfo.ArgumentList.Add("--redact=100");
// ParseJsonFinding reads RuleID, File, the range, Description and Fingerprint —
// never the Secret or Match fields the report also carries.
return new SecurityFindingRecord(ruleId, severity, description, location, Evidence: null, path, accepted);
```

## Bad example

```csharp
catch (IOException exception)
{
    // Hands the caller the host path and the scanner's raw output, secret included.
    return Results.Problem(detail: exception.ToString() + "\n" + process.StandardOutput.ReadToEnd());
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the structural redaction in
  `GitleaksSecurityScanner` and the single-message exception policy in `QualityStudio.Api`.
