---
id: QS-DN-001
title: Expose HTTP routes as typed minimal-API delegates, not ad-hoc controllers
technology: .NET
category: api-shape
kinds: [code]
severity: low
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

Add new HTTP endpoints as `app.MapGet`/`MapPost`/`MapPut`/`MapDelete` delegates
with strongly typed parameters resolved from DI and route/query binding, in
the same minimal-API style as the rest of `QualityStudio.Api`, rather than
introducing an MVC controller class or an untyped `HttpContext`-only handler.

## Rationale

`QualityStudio.Api` is entirely minimal APIs; adding a controller-based
endpoint would introduce a second routing model with different filter,
model-binding, and testing conventions that the rest of the project doesn't
use, for no behavioral benefit. Typed parameters (`bool? includeArchived`,
`RepositoryRegistry registry`) let the framework do the binding and null
checks that a raw `HttpContext.Request.Query["..."]` read would otherwise
require by hand.

## Good example

```csharp
// Program.cs:212
app.MapGet("/api/repos", (HttpContext context, bool? includeArchived, RepositoryRegistry registry,
    RepositorySnapshotPrewarmer prewarmer, ApiSecurity security) =>
{
    var repositories = registry.List(includeArchived == true)
        .Where(repository => security.Identity(context).CanAccess(repository.Id))
        .ToArray();
    return Results.Ok(new { repositories });
});
```

## Bad example

```csharp
app.MapGet("/api/repos", (HttpContext context) =>
{
    var includeArchived = context.Request.Query["includeArchived"] == "true";
    var registry = context.RequestServices.GetRequiredService<RepositoryRegistry>();
    return Results.Ok(registry.List(includeArchived));
    // no typed binding, no injected ApiSecurity check, resolves services by hand
});
```
