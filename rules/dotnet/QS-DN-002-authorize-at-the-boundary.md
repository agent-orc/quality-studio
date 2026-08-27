---
id: QS-DN-002
title: Check access at the endpoint, not deep inside a service
technology: .NET
category: api-shape
kinds: [code, security]
severity: critical
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

Every endpoint that returns or mutates repository-scoped data resolves
`ApiSecurity` and checks `security.Identity(context).CanAccess(...)` before
returning data, filtering a list, or performing the mutation — at the
endpoint delegate, not several calls deep inside a service that has no way
to know which caller it's serving.

## Rationale

An authorization check placed deep inside a shared service is easy to miss
for the next endpoint that calls the same service, because nothing at the
call site signals that the check is the service's responsibility rather than
the caller's. Checking at the boundary — where the caller's identity is
naturally in scope as `HttpContext` — means every new endpoint has an
unmissable, uniform place to put the check, and a reviewer can verify
authorization by reading the endpoint alone.

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
app.MapGet("/api/repos/{repoId}/risk", (string repoId, RepositoryRegistry registry) =>
    Results.Ok(registry.GetRisk(repoId)));
    // no ApiSecurity check at all — relies on GetRisk() to enforce access,
    // but GetRisk() has no HttpContext and cannot know who is asking
```
