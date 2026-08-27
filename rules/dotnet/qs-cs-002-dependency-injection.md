---
id: QS-CS-002
title: Take collaborators through constructor injection
summary: Services declare their dependencies as constructor parameters registered in the container; they do not new up collaborators or reach for a service locator.
technology: dotnet
category: dependency-injection
severity: medium
autofixable: false
defaultOn: false
kinds: [code]
levels: [file]
version: 1.0.0
status: active
---
## Statement

A class that needs another service declares it as a constructor parameter
and the composition root (`Program.cs`) registers both with an explicit
lifetime (`AddSingleton`/`AddScoped`/`AddTransient`). A class MUST NOT call
`new` on a collaborator that has side effects (I/O, other services) or pull
a dependency from `IServiceProvider` inside a method body.

## Rationale

Constructor injection is what makes a class unit-testable without a running
host: every collaborator can be swapped for a fake at the call site. It also
makes the dependency graph visible at a glance — reading the constructor
signature tells you everything the class can do — instead of hidden inside
method bodies where a `new SomeSensor()` silently reintroduces a
dependency the container doesn't know about and can't override.

## Good example

`src/QualityStudio.Api/RepositoryRegistry.cs:50`:

```csharp
public RepositoryRegistry(IHostEnvironment environment, IOptions<RepositoryOptions> options,
    SensorRegistry sensors, ILogger<RepositoryRegistry> logger, ReviewMetaIndex metaIndex)
```

Every collaborator is declared, and `Program.cs:26` registers it
(`builder.Services.AddSingleton<RepositoryRegistry>();`). Where a concrete
type needs to satisfy an interface, the registration forwards to the already
-registered singleton instead of constructing a second instance:

```csharp
builder.Services.AddSingleton<IReviewSensor>(sp => sp.GetRequiredService<GitleaksSecurityScanner>());
```

## Bad example

```csharp
public sealed class RepositoryRegistry
{
    private readonly SensorRegistry sensors = new(); // side-effecting collaborator, not injected

    public async Task<RepositoryRegistration> CreateAsync(RepositoryRegistrationRequest request, CancellationToken ct)
    {
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("RepositoryRegistry"); // built per-call
        ...
    }
}
```

Both the sensor registry and the logger are now invisible to the container
and impossible to substitute in a test without touching process-wide state.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in Quality Studio's own API
  during the QS-90 rule-library seeding session.
