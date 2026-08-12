---
id: QS-CS-001
title: Keep API contracts explicit and transport-safe
language: csharp-dotnet
severity: high
autofixable: false
default-enabled: true
version: 1.0.0
status: active
kinds: [code, security]
levels: [file, module, project]
applies-to: [.cs]
references: [src/QualityStudio.Api/ApiContracts.cs, src/QualityStudio.Api/Program.cs, Agent Studio backend/Shared/Models/TaskRequests.cs]
deterministic-check: none
---

## Statement

Represent request and response shapes with explicit typed contracts, validate untrusted values at the API boundary, and map expected failures to stable transport responses. Do not expose persistence models, absolute server paths, exception details, or ambiguous anonymous shapes as public contracts.

## Rationale

Quality Studio separates API response records from core models and confines repository paths before use. Agent Studio similarly centralizes task request contracts. Explicit shapes make compatibility, authorization, serialization, and client behavior reviewable.

## Bad example

```csharp
app.MapPost("/api/run", (dynamic body) => service.Start(body));
```

## Good example

```csharp
public sealed record StartRunRequest(string Path, string Kind);
public sealed record StartRunResponse(string Id, string State);

app.MapPost("/api/run", async (StartRunRequest request, CancellationToken ct) =>
    Results.Ok(await service.StartAsync(request, ct)));
```

## Change history

- 2026-08-12, v1.0.0: Initial C# API-shape rule grounded in both Studio API layers.
