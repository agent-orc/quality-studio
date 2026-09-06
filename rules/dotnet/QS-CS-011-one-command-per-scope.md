---
id: QS-CS-011
version: 1.0.0
title: Run one command per scope, not one per project or file
technology: dotnet
kinds: [performance]
category: n-plus-one
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

When a tool can answer for a whole solution or repository, invoke it once and project its result
onto the units that need it. Do not launch a process, open a connection, or repeat a
repository-wide scan inside a loop over projects, files, or findings.

## Rationale

`DependencyVulnerabilitySensor` runs `dotnet list <project> package --vulnerable` once per
discovered project where one solution-wide command would answer the same question, and the review
runner invokes repository-wide security sensors inside each file operation and then filters the
result down to that one file — a 92-file sweep repeats the whole repository scan 92 times. Process
start-up and repository traversal dominate these costs, so the loop, not the tool, is what makes
the sweep slow.

## Detection

Look for a process start, an HTTP call, a database round trip, or a full-repository traversal
inside a `foreach` over projects, files, or findings, and for a call whose result is immediately
filtered to a single subject. Batch APIs that genuinely take one subject per call are not a
violation; repeating a call that already accepts the whole scope is.

## Good example

```csharp
// One repository-wide scan, projected onto the subjects that need it.
var evidence = request.DeterministicEvidence
    ?? await CollectDeterministicEvidenceAsync(request, root, cancellationToken).ConfigureAwait(false);
var forThisUnit = DeterministicEvidenceProjection.ForSubjects(evidence, subjectPaths);
```

## Bad example

```csharp
foreach (var project in discoveredProjects)   // one process per project
{
    var result = await runner.RunAsync("dotnet",
        ["list", project, "package", "--vulnerable", "--format", "json"], root, cancellationToken);
    findings.AddRange(Parse(result));
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the per-project `dotnet list` invocations in
  `DependencyVulnerabilitySensor` and the per-file repository scans recorded in
  `docs/operations/static-analysis/`.
