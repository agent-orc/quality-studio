---
id: QS-CS-003
version: 1.0.0
title: Propagate CancellationToken; never write async void
technology: dotnet
category: async-hygiene
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.0.0
---

## Statement

Every `async` method that does I/O accepts a `CancellationToken` and passes it to every awaited
call that accepts one. `async` methods return `Task`/`Task<T>` (or `ValueTask`/`ValueTask<T>`) —
never `async void`, except for a framework-mandated event handler. Do not block on async work
with `.Result`, `.Wait()`, or `GetAwaiter().GetResult()`.

## Rationale

`GuidelineImpactAnalyzer.AnalyzeAsync(string, GuidelineImpactRequest, CancellationToken, ...)`
and `ReviewRunner`'s `Async` methods thread a `CancellationToken` through every awaited call so a
caller can actually cancel a long-running review. `async void` swallows exceptions instead of
surfacing them on the returned task, and sync-over-async blocking can deadlock a request thread
under load — both defeat the cancellation and error-propagation guarantees the rest of the
codebase already relies on.

## Good example

```csharp
public virtual async Task<GuidelineImpactResult> AnalyzeAsync(
    string repositoryRoot, GuidelineImpactRequest request, CancellationToken cancellationToken)
{
    var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    // ...
}
```

## Bad example

```csharp
public async void Refresh() // async void: exceptions never surface to the caller
{
    var content = File.ReadAllText(path); // blocking I/O on an async method
    var result = LoadAsync().Result;      // sync-over-async: can deadlock
}
```

## Change history

- 1.0.0 (2026-08-27): Initial rule, grounded in the `CancellationToken`-propagating signatures of
  `GuidelineImpactAnalyzer.AnalyzeAsync` and `ReviewRunner`'s async methods.
