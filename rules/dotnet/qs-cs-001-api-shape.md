---
id: QS-CS-001
title: Keep Minimal API endpoint handlers thin
summary: An endpoint delegate should validate/dispatch and shape the HTTP result; business logic belongs in an injected service, not the lambda body.
technology: dotnet
category: api-shape
severity: medium
autofixable: false
defaultOn: false
kinds: [code]
levels: [file, function]
version: 1.0.0
status: active
---
## Statement

A `MapGet`/`MapPost`/`MapPut` delegate should read as: bind parameters, call
one injected service method, translate the result to a `Results.*` response.
When a handler starts branching on business rules, composing multiple
service calls with intermediate logic, or building the response payload by
hand from several sources, extract that into a service method the handler
simply calls.

## Rationale

Minimal API handlers are not unit-testable in isolation the way a plain
service class is — testing handler logic means spinning up
`WebApplicationFactory`. Keeping the delegate thin means the actual behavior
lives in a class that is trivially unit-testable, and the endpoint map stays
a readable table of routes instead of a second copy of the domain layer.

## Good example

`src/QualityStudio.Api/Program.cs:228`:

```csharp
app.MapPost("/api/repos", async (RepositoryRegistrationRequest request, RepositoryRegistry registry,
    RepositorySnapshotPrewarmer prewarmer, CancellationToken cancellationToken) =>
{
    var created = await registry.CreateAsync(request, cancellationToken);
    prewarmer.Queue(created);
    return Results.Created($"/api/repos/{created.Id}", created);
});
```

Registration, queuing, and response shaping — no validation or branching
logic lives in the delegate; `RegistryCreateAsync` owns that.

## Bad example

```csharp
app.MapPost("/api/repos", async (RepositoryRegistrationRequest request, RepositoryRegistry registry,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Root) || !Directory.Exists(request.Root))
        return Results.BadRequest("Root must exist.");
    if (registry.List().Any(r => r.Root == request.Root))
        return Results.Conflict("Already registered.");
    var id = Guid.NewGuid().ToString("N")[..8];
    var created = new RepositoryRegistration(id, request.Root, DateTimeOffset.UtcNow);
    await registry.SaveAsync(created, cancellationToken);
    return Results.Created($"/api/repos/{id}", created);
});
```

Validation, uniqueness checking, and ID assignment are domain rules that now
live only in the endpoint map, invisible to anything that isn't an HTTP test.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in Quality Studio's own API
  during the QS-90 rule-library seeding session.
