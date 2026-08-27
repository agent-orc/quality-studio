---
id: QS-DN-006
title: Only bridge sync-to-async at a genuine synchronous entry point
technology: .NET
category: async-hygiene
kinds: [code]
severity: high
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

Do not call `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on a task
from within code that is itself already `async`. The pattern is only
acceptable at a genuinely synchronous entry point that has no async caller
above it to propagate `await` to (a CLI `Main`, or a narrow static helper
called only from such entry points) — and even there, prefer making the
entry point `async Task` instead when the surrounding API allows it.

## Rationale

Blocking on a task from inside an async call chain is the classic ASP.NET/UI
deadlock: the blocked thread can be the same one the awaited continuation
needs to resume on, so the call never returns under load even though it
works fine in a quick manual test. This codebase has exactly two current
uses of `.GetAwaiter().GetResult()`, and both are narrow, defensible cases:
`quality/Program.cs`'s CLI entry point (there is no async `Main` caller above
it to propagate to) and `GitPlumbing.RequireRepository`, a synchronous helper
called from non-async constructors. Neither is called from within already-
running async code — that distinction, not the mere presence of
`GetAwaiter().GetResult()`, is what a review of a new use of this pattern
needs to verify.

## Good example

```csharp
// GitPlumbing.cs:172 — a synchronous helper with no async caller to propagate to
public static string RequireRepository(string path)
{
    var top = RunAsync(root, ["rev-parse", "--show-toplevel"], CancellationToken.None)
        .GetAwaiter().GetResult().Trim();
    return Path.GetFullPath(top);
}
```

## Bad example

```csharp
public async Task<RepositoryRegistration> CreateAsync(RepositoryRegistrationRequest request, CancellationToken cancellationToken)
{
    // blocking inside an already-async method: can deadlock the request thread
    var validated = ValidateRemoteAsync(request).GetAwaiter().GetResult();
    return await PersistAsync(validated, cancellationToken);
}
```
