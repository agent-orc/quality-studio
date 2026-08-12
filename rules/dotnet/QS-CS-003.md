---
id: QS-CS-003
title: Keep asynchronous flows cancellation-aware and non-blocking
language: csharp-dotnet
severity: high
autofixable: false
default-enabled: true
version: 1.0.0
status: active
kinds: [code, performance]
levels: [file, module, project]
applies-to: [.cs]
references: [src/AgentOrchestrator.CodeQuality/ReviewRunner.cs, src/QualityStudio.Api/ReviewJobs.cs, Agent Studio backend runner services]
deterministic-check: none
---

## Statement

Use asynchronous APIs end to end for I/O, accept and propagate `CancellationToken`, and avoid `.Result`, `.Wait()`, sync-over-async, fire-and-forget work, and unbounded concurrency. Cancellation must stop work without being reported as an ordinary failure.

## Rationale

Quality Studio threads cancellation through review, sensor, storage, and API operations. Agent Studio's runner services also depend on cancellation-aware orchestration. Blocking or detached work can deadlock, outlive its run, corrupt lifecycle reporting, or exhaust shared resources.

## Bad example

```csharp
public Result Review(string path) => ReviewAsync(path).Result;
```

## Good example

```csharp
public async Task<Result> ReviewAsync(string path, CancellationToken cancellationToken)
{
    var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    return await reviewer.ReviewAsync(content, cancellationToken).ConfigureAwait(false);
}
```

## Change history

- 2026-08-12, v1.0.0: Initial .NET asynchronous-hygiene rule.
