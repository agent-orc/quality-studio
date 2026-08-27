---
id: QS-DN-007
title: Use TestContext's cancellation token and clean up fixtures in finally
technology: .NET
category: test-structure
kinds: [code]
severity: medium
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

An async xUnit test that performs I/O passes `TestContext.Current.CancellationToken`
to it (not `default`/`CancellationToken.None`), and any temporary fixture it
creates on disk is deleted in a `finally` block so a failing assertion still
leaves the temp directory clean.

## Rationale

`TestContext.Current.CancellationToken` is xUnit v3's per-test cancellation
token — it's what makes a test actually stop promptly when the run is
cancelled or times out, instead of leaking a task that keeps running after
the test framework has already reported a result. A `finally`-guarded
`Directory.Delete(root, true)` guarantees a fixture doesn't outlive one test
and pollute the next, or accumulate across a full test-suite run, even when
the test fails partway through.

## Good example

```csharp
// ProjectDashboardTests.cs
var root = TemporaryRepository();
try
{
    await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), "...",
        TestContext.Current.CancellationToken);
    // ...assertions...
}
finally
{
    Directory.Delete(root, true);
}
```

## Bad example

```csharp
var root = TemporaryRepository();
await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), "...", CancellationToken.None);
var dashboard = new ProjectDashboardService().Get(root, hierarchy);
Assert.Equal(5, dashboard.Metrics.FileCount);
Directory.Delete(root, true);
// an assertion failure above skips this line — root is left on disk for every failed run
```
