---
id: QS-DN-004
title: Use an interface when a service is collected polymorphically or must be swappable
technology: .NET
category: di-patterns
kinds: [code]
severity: low
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

When a new capability has (or will predictably have) more than one runtime
implementation that callers should treat uniformly — either because the
container injects all of them as a collection, or because a factory decides
which one to use — define an interface and register each concrete type
against it, following the `IReviewSensor` pattern.

## Rationale

The counterpart to [QS-DN-003](../dotnet/QS-DN-003-register-concrete-by-default.md):
this codebase's sensors (`GitleaksSecurityScanner`, `DependencyVulnerabilitySensor`,
`BoundaryInventorySensor`, `CoverageSensor`, `SarifSensor`, `RoslynAnalyzerSensor`,
`EslintAnalyzerSensor`, `TypeScriptAnalyzerSensor`) all implement `IReviewSensor`
and are registered as that interface specifically so the review pipeline can
depend on `IEnumerable<IReviewSensor>` and run "every sensor" without knowing
the concrete list. Without the interface, adding a ninth sensor would require
editing every call site that enumerates sensors by hand instead of just
adding one more `AddSingleton<IReviewSensor>(...)` registration.

## Good example

```csharp
// Program.cs:48-55 — eight concrete sensors, one collected interface
builder.Services.AddSingleton<IReviewSensor>(sp => sp.GetRequiredService<GitleaksSecurityScanner>());
builder.Services.AddSingleton<IReviewSensor>(sp => sp.GetRequiredService<DependencyVulnerabilitySensor>());
builder.Services.AddSingleton<IReviewSensor>(sp => sp.GetRequiredService<RoslynAnalyzerSensor>());
// consumed elsewhere as IEnumerable<IReviewSensor>, with no per-sensor call sites to update
```

## Bad example

```csharp
public sealed class SensorRunner
{
    // adding a sensor here means finding and editing this constructor and every
    // caller that lists sensors by name, instead of adding one DI registration
    public SensorRunner(GitleaksSecurityScanner a, DependencyVulnerabilitySensor b,
        BoundaryInventorySensor c, CoverageSensor d) { /* ... */ }
}
```
