---
id: QS-CS-002
version: 1.1.0
title: Register the narrowest correct DI lifetime; inject via constructor
technology: dotnet
kinds: [code]
category: dependency-injection
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.0.0
---

## Statement

Register a service as `Singleton` only when it is stateless or its internal state is safe to
share across all requests; register per-operation or short-lived collaborators as `Transient`
(or `Scoped` where request-scoped state is genuinely needed). Consume dependencies through
constructor (or primary-constructor) parameters — never resolve them ad hoc via
`IServiceProvider.GetService`/`GetRequiredService` inside a class that could take them as
constructor parameters instead.

## Rationale

`backend/src/QualityStudio.Api/Program.cs` registers `GuidelineStore` as `Singleton` (stateless,
delegates to the filesystem per call) but `GuidelineImpactAnalyzer` as `Transient` (does
per-analysis work); mismatching this — e.g. making a per-request analyzer a singleton — risks
leaking state across unrelated requests. Constructor injection keeps a class's true
dependencies visible in its signature and keeps it trivially constructible in unit tests
without a DI container.

## Detection

Read the `builder.Services.Add*` registration next to the type's actual state. Flag `AddSingleton` on a type holding per-operation mutable fields, and any `GetService`/`GetRequiredService` call inside a class that already has a constructor able to take the dependency. `IServiceProvider` use inside composition-root code is not a violation.

## Good example

```csharp
// Program.cs
builder.Services.AddSingleton<GuidelineStore>();
builder.Services.AddTransient<GuidelineImpactAnalyzer>();

// ReviewJobs.cs — dependencies arrive as primary-constructor parameters
public sealed class ReviewExecutorFactory(
    SensorRegistry sensors,
    StalenessEvaluator stalenessEvaluator) : IReviewExecutorFactory
```

## Bad example

```csharp
public sealed class ReviewExecutorFactory : IReviewExecutorFactory
{
    public IReviewExecutor Create(IServiceProvider provider, ...) =>
        new ReviewExecutor(new ReviewRunner(sensorRegistry: provider.GetRequiredService<SensorRegistry>()));
        // hides the real dependency behind a service-locator pull instead of a constructor parameter
}
```

## Change history

- 1.1.0 (2026-09-06): Declared the applicable review kinds and added detection guidance for the generated catalogue.
- 1.0.0 (2026-08-27): Initial rule, grounded in the `Singleton`/`Transient` split for
  `GuidelineStore`/`GuidelineImpactAnalyzer` and the primary-constructor DI pattern used by
  `ReviewExecutorFactory` in `backend/src/QualityStudio.Api/Program.cs` and `ReviewJobs.cs`.