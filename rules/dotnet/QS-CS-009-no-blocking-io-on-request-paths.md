---
id: QS-CS-009
version: 1.0.0
title: Keep blocking I/O and process waits off request paths and out of locks
technology: dotnet
kinds: [performance]
category: request-path
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

Code that runs while a request is open uses the asynchronous file, stream, and process APIs and
awaits them with a `CancellationToken`. No `File.ReadAllText`, `StandardOutput.ReadToEnd`,
`WaitForExit()`, or `.Result` on a request path — and none of them inside a `lock`, which converts
one slow call into a queue for every other caller of the same gate.

## Rationale

Quality Studio's browser contract is under 100 ms to a visible transition and under 500 ms to a
usable dashboard, and `PERF.md` measures a cold hierarchy scan at 85% of a repository switch, so a
request path has no room for a thread parked on a disk or a child process. Blocking inside a held
lock is the worse half: `RepositoryHierarchyCache` reads every changed file while holding the slot
gate, which makes a multi-second scan a multi-second wait for every concurrent switch of that
repository, not just the one that paid for it.

## Detection

Look for the synchronous `File`, `Directory`, `Stream`, and `Process` members in anything an
endpoint, a handler, or a background reader can reach, for `.Result`, `.Wait()`, and
`GetAwaiter().GetResult()`, and for any of them lexically inside a `lock` block or between a
`Semaphore.Wait` and its release. Synchronous I/O in start-up code, a CLI command, or a test
fixture is not a violation.

## Good example

```csharp
// backend/QualityStudio.Api/RepositorySnapshotPrewarmer.cs
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Keep host startup non-blocking: the API becomes reachable while snapshots warm in the background.
    await Task.Yield();
    await foreach (var registration in queue.Reader.ReadAllAsync(stoppingToken))
    {
        var hierarchy = await Task.Run(() => hierarchyCache.GetMeasured(registration.Root), stoppingToken);
    }
}
```

## Bad example

```csharp
lock (slot.Gate)                                  // every other caller of this slot now waits too
{
    var head = process.StandardOutput.ReadToEnd();     // blocking read of a child process
    process.WaitForExit();                             // no token, no timeout
    foreach (var path in changed) hash.Append(File.ReadAllText(path));
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the `PERF.md` switch budget and the blocking
  git and file reads `RepositoryHierarchyCache` performs while holding its slot gate.
