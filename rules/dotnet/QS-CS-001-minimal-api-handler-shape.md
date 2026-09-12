---
id: QS-CS-001
version: 1.1.0
title: Shape minimal-API endpoints as typed static handlers
technology: dotnet
kinds: [code]
category: api-shape
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.0.0
---

## Statement

A minimal-API endpoint is a `static` (or local) handler function that takes its dependencies as
parameters (`HttpContext`, registered services, route/body values) via ASP.NET Core's parameter
binding, and returns a typed `IResult` (`Results.Ok`, `Results.Created`, `Results.NoContent`,
...). Route registration (`app.MapGet`/`MapPost`/...) stays a one-line pointer to that handler.

## Rationale

`backend/QualityStudio.Api/Program.cs` already establishes this shape consistently (`Guidelines`,
`InstallGuideline`, `CreateGuideline`, ...): the handler is independently testable without
spinning up the HTTP pipeline, dependencies are explicit in the signature instead of pulled from
an ambient service locator, and every route returns the same small set of typed results the
framework can serialize predictably.

## Detection

Look at the `app.Map*` registrations for inline lambdas with bodies longer than one expression, and at handlers for `IServiceProvider`/`GetRequiredService` use, `HttpContext.Response` written by hand, or a return type that is not `IResult`/`Task<IResult>`. A one-line lambda that forwards to a named handler is the intended shape.

## Good example

```csharp
static IResult InstallGuideline(HttpContext context, string catalogueId, RepositoryRegistry registry, GuidelineStore store)
{
    var (_, repository) = ResolveRepository(context, registry);
    var installed = store.Install(repository.Root, catalogueId);
    return Results.Created($"{context.Request.PathBase}/api/guidelines/{Uri.EscapeDataString(installed.Id)}", installed);
}
```

## Bad example

```csharp
app.MapPost("/api/widgets", async (HttpContext context) =>
{
    var store = context.RequestServices.GetRequiredService<WidgetStore>(); // service-locator pull
    var body = await JsonSerializer.DeserializeAsync<WidgetRequest>(context.Request.Body);
    await store.SaveAsync(body!);
    context.Response.StatusCode = 201; // untyped result, no IResult
});
```

## Change history

- 1.1.0 (2026-09-06): Declared the applicable review kinds and added detection guidance for the generated catalogue.
- 1.0.0 (2026-08-27): Initial rule, grounded in the handler-function convention used throughout
  `backend/QualityStudio.Api/Program.cs`.