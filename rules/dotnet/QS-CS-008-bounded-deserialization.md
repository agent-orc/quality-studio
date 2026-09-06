---
id: QS-CS-008
version: 1.0.0
title: Gate and bound every deserialization of data you did not write
technology: dotnet
kinds: [security]
category: deserialization
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

Deserializing a document that came from a repository, a request, or another process checks its
schema id and version before its content is used, rejects members the contract does not declare
(`JsonUnmappedMemberHandling.Disallow`), and bounds what it will read — a size limit before the
bytes are loaded, an explicit `MaxDepth`, and no `AllowTrailingCommas` shortcut.

## Rationale

`QualityRunReport` sets `UnmappedMemberHandling.Disallow` and refuses a document whose
`schemaVersion` or `$schema` is not the one it understands, so an old or foreign document fails
loudly instead of binding half its fields; `ReviewMetaContract`, `QualityFindingContract`, and
`FindingStateStore` follow the same shape. The missing half is size: reading a repository file
into a string with no cap lets one caller exhaust process memory by repeating the request, and
nothing in this codebase sets `MaxDepth`, so the 64-level default is the only depth bound there is.

## Detection

Look for `JsonSerializer.Deserialize`, `JsonDocument.Parse`, or `File.ReadAllText`/`ReadAllBytes`
on a path or stream the process does not own, and check for three things before the result is used:
a schema and version gate, unmapped-member rejection, and a byte or length limit. A round-trip of a
document this process just wrote is not a violation.

## Good example

```csharp
// src/AgentOrchestrator.CodeQuality/QualityRunReport.cs
private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.Default,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
};

if (report.SchemaVersion != 1 || !string.Equals(report.Schema, SchemaId, StringComparison.Ordinal))
    throw new JsonException("Unsupported quality run report schema.");
```

## Bad example

```csharp
// No size check, no depth bound, no version gate: the file decides how much memory this costs.
var text = await File.ReadAllTextAsync(sidecarPath, cancellationToken);
var meta = JsonSerializer.Deserialize<ReviewMeta>(text)!;
return meta.Findings;
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the version-gated, unmapped-member-rejecting
  options in `QualityRunReport` and the unbounded read recorded as a finding against
  `QualityStudio.Api`'s file endpoint.
