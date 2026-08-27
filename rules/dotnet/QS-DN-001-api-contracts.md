---
id: QS-DN-001
version: 1.0.0
title: Keep API contracts explicit and stable
language: csharp
kinds: [code]
appliesTo: [**/*.cs]
severity: high
defaultOn: true
autofixable: false
deterministic: false
---

## Statement

Represent API inputs and outputs with explicit typed contracts, validate untrusted values at the boundary, preserve documented JSON names and status semantics, and do not expose persistence or domain implementation types directly.

## Rationale

Quality Studio's API is consumed independently by its Angular surface and Agent Studio. Typed request and response records make compatibility reviewable, keep validation at the HTTP boundary, and prevent internal refactors from becoming accidental protocol changes.

## Bad example

```csharp
app.MapPost("/api/reviews", (dynamic body) => store.Save(body));
```

## Good example

```csharp
app.MapPost("/api/reviews", async (
    StartReviewRequest request,
    ReviewJobService jobs,
    CancellationToken cancellationToken) =>
    Results.Accepted(value: await jobs.EnqueueAsync("default", request, cancellationToken)));
```

## Change history

- 2026-08-12: Initial .NET seed grounded in Quality Studio's minimal API contracts.
