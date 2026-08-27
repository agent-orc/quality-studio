---
id: QS-CS-003
title: Propagate CancellationToken; never block on async work
summary: Awaited calls must forward the caller's CancellationToken, and library code must never block a thread on async work with .GetAwaiter().GetResult().
technology: dotnet
category: async-hygiene
severity: high
autofixable: true
defaultOn: true
kinds: [code]
levels: [file, function]
version: 1.0.0
status: active
deterministicCheck: {tool: roslyn, ruleId: CA2016}
---
## Statement

Every async call that accepts a `CancellationToken` MUST be given the one
that flowed into the enclosing method, not `CancellationToken.None`.
Synchronous callers of async APIs MUST NOT block with
`.GetAwaiter().GetResult()` (or `.Result`/`.Wait()`); either make the caller
async end-to-end or provide a genuinely synchronous implementation.

## Rationale

Dropping the token to `CancellationToken.None` silently defeats cancellation
for that entire call subtree — a caller that cancels will keep waiting on
work it explicitly asked to stop. Sync-over-async blocking a thread pool
thread on awaited I/O is a well-known way to exhaust the thread pool under
load and can deadlock in the presence of a synchronization context. Both are
easy to introduce accidentally when a synchronous method needs to call an
async API and "just add `.GetAwaiter().GetResult()`" is the path of least
resistance.

## Good example

`src/AgentOrchestrator.CodeQuality/DependencyVulnerabilitySensor.cs:59`:

```csharp
await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
return new SensorCommandResult(process.ExitCode,
    await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
```

The caller's token flows all the way down, and `ConfigureAwait(false)` keeps
library code off the calling context.

## Bad example

`src/AgentOrchestrator.CodeQuality/GitChangeSetProvider.cs:175` (real, from
this repository):

```csharp
public static string RequireRepository(string path)
{
    var root = Path.GetFullPath(path);
    var top = RunAsync(root, ["rev-parse", "--show-toplevel"], CancellationToken.None)
        .GetAwaiter().GetResult().Trim();
    ...
}
```

`RequireRepository` is a synchronous static method that blocks on
`RunAsync` and hardcodes `CancellationToken.None`, so a caller's
cancellation request cannot reach the `git` process this launches.

## Notes

Roslyn's `CA2016` ("forward the CancellationToken parameter") has a working
code fix for the missing-token case, which is why this rule is marked
`autofixable: true` for that half of the violation; converting a blocking
sync-over-async call site to be properly async is a design change and needs
a human.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in a real violation found in
  Quality Studio's own codebase during the QS-90 rule-library seeding
  session.
