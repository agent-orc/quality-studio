---
id: QS-DN-003
title: Register a service by its concrete type unless it needs to vary
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

Register a new service with `AddSingleton<TConcrete>()` (or `AddTransient`/
`AddScoped`) using its concrete class, not an `IThing`/`ThingImpl` pair,
unless the service either has more than one runtime implementation or is
consumed as part of an injected collection.

## Rationale

Most services in `QualityStudio.Api` (`InputResolver`, `GuidelineStore`,
`AttackCatalogueResolver`, `ProjectDashboardService`, ...) are registered by
concrete type — there is exactly one implementation, and an interface would
be pure indirection with nothing to swap. The codebase reserves interfaces
for the two cases where they earn their cost: a fan-in collection resolved
polymorphically (`IReviewSensor`, registered from seven different concrete
sensor types and injected as `IEnumerable<IReviewSensor>`), and a component
meant to be genuinely swappable (`IReviewExecutorFactory` /
`ReviewExecutorFactory`). Introducing an interface for every new service "for
testability" when the concrete class is already easy to construct in a test
just adds a file and a name to keep in sync for no isolation benefit.

## Good example

```csharp
// Program.cs:34-36 — one implementation each, registered concretely
builder.Services.AddSingleton<InputResolver>();
builder.Services.AddSingleton<GuidelineStore>();
builder.Services.AddTransient<GuidelineImpactAnalyzer>();
```

## Bad example

```csharp
public interface IInputResolver { ResolvedInputs Resolve(...); }
public sealed class InputResolver : IInputResolver { /* only implementation, ever */ }
builder.Services.AddSingleton<IInputResolver, InputResolver>();
// no second implementation, nothing consumes IInputResolver polymorphically —
// the interface adds a layer of indirection with no corresponding decision point
```
