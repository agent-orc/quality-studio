---
id: QS-DN-002
version: 1.0.0
title: Use constructor injection and intentional lifetimes
language: csharp
kinds: [code]
appliesTo: [**/*.cs]
severity: medium
defaultOn: true
autofixable: false
deterministic: false
---

## Statement

Declare runtime dependencies through constructor injection, register them with a lifetime that matches their state and thread-safety, and avoid service location, hidden static state, or constructing infrastructure clients inside business logic.

## Rationale

Explicit dependency graphs make Quality Studio services testable and make concurrency assumptions visible. Singleton registries and caches must be thread-safe; request-specific or mutable work must not leak across reviews through an over-broad lifetime.

## Bad example

```csharp
public Task RunAsync() {
    var client = new HttpClient();
    var store = ServiceLocator.Get<ReviewRunStore>();
    return store.SaveAsync();
}
```

## Good example

```csharp
public sealed class ReviewService(HttpClient client, ReviewRunStore store)
{
    public Task RunAsync(CancellationToken cancellationToken) =>
        store.SaveAsync(client, cancellationToken);
}
```

## Change history

- 2026-08-12: Initial .NET seed grounded in Quality Studio's host registration and service constructors.
