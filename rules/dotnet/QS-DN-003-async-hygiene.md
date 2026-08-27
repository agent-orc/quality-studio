---
id: QS-DN-003
version: 1.0.0
title: Keep async paths non-blocking and cancellation-aware
language: csharp
kinds: [code, performance]
appliesTo: [**/*.cs]
severity: high
defaultOn: true
autofixable: false
deterministic: true
---

## Statement

Use async end-to-end for I/O and queued work, propagate `CancellationToken` through every cancellable boundary, and never block a task with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on request or worker paths.

## Rationale

Quality Studio runs concurrent reviews and long-lived sensors. Blocking async work can starve the host, cancellation loss wastes agent and analyzer work, and fire-and-forget tasks make run state unreliable.

## Bad example

```csharp
var report = scanner.RunAsync(request).Result;
repository.SaveAsync(report).Wait();
```

## Good example

```csharp
var report = await scanner.RunAsync(request, cancellationToken).ConfigureAwait(false);
await repository.SaveAsync(report, cancellationToken).ConfigureAwait(false);
```

## Change history

- 2026-08-12: Initial default-on rule with deterministic checks for blocking task consumption.
