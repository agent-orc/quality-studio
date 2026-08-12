---
id: QS-CS-002
title: Use dependency injection with truthful lifetimes
language: csharp-dotnet
severity: medium
autofixable: false
default-enabled: true
version: 1.0.0
status: active
kinds: [code]
levels: [file, module, project]
applies-to: [.cs]
references: [src/QualityStudio.Api/Program.cs, src/QualityStudio.Api/ReviewJobs.cs, Agent Studio backend/Host/Program.cs]
deterministic-check: none
---

## Statement

Inject services through constructors, register interfaces at the composition root, and choose singleton, scoped, or transient lifetime from actual state and thread-safety. Do not create service graphs with `new` inside business logic or capture shorter-lived dependencies in longer-lived services.

## Rationale

Both Studio backends keep registrations in their host composition roots and inject orchestration dependencies. Truthful lifetimes make ownership, test replacement, concurrency, and disposal behavior explicit.

## Bad example

```csharp
public Task RunAsync() {
    var store = new ReviewRunStore(Environment.CurrentDirectory);
    return store.SaveAsync();
}
```

## Good example

```csharp
public sealed class ReviewService(IReviewRunStore store)
{
    public Task RunAsync(CancellationToken cancellationToken) =>
        store.SaveAsync(cancellationToken);
}
```

## Change history

- 2026-08-12, v1.0.0: Initial .NET dependency-injection rule.
