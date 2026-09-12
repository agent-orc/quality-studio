---
id: QS-CS-006
version: 1.0.0
title: Start external processes from a fixed executable with an argument list
technology: dotnet
kinds: [security]
category: process-execution
severity: critical
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

The executable of a spawned process comes from code or a host-owned allowlist, never from
repository or request data. Arguments go into `ProcessStartInfo.ArgumentList` one at a time —
never a concatenated `Arguments` string — with `UseShellExecute = false`, `CreateNoWindow = true`,
redirected streams, and a `CancellationToken` on the wait.

## Rationale

`ProcessSensorCommandRunner.RunAsync`, `GitleaksSecurityScanner.RunScanAsync`, and
`GitleaksBinaryResolver.RunVersionAsync` all use `ArgumentList`, so a repository path containing a
space or a quote is an argument and not a new command. The executable is the part that argument
escaping cannot protect: a configured command string lets a repository name a shell, and that
process reads beyond its working directory, inherits the host's credentials, reaches the network,
and writes host-visible files — the whole confinement story above it is then decoration.

## Detection

Look for `ProcessStartInfo.Arguments` assigned from an interpolated string, for the executable
being read from configuration, a request, or a repository file, and for `WaitForExit()` without a
token or a timeout. `ArgumentList.Add` on values that only reach the child as data is the intended
shape; the file name is what must be fixed.

## Good example

```csharp
// backend/src/AgentOrchestrator.CodeQuality/DependencyVulnerabilitySensor.cs
StartInfo = new ProcessStartInfo(executable)
{
    WorkingDirectory = workingDirectory,
    RedirectStandardOutput = true, RedirectStandardError = true,
    UseShellExecute = false, CreateNoWindow = true,
},
...
foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
```

## Bad example

```csharp
var command = configuration.AnalyzerCommand;              // free-form, repository-owned
var parts = command.Split(' ');
using var process = Process.Start(new ProcessStartInfo(parts[0])
{
    Arguments = string.Join(' ', parts.Skip(1)) + " " + target,   // one quote away from a new command
});
process!.WaitForExit();                                   // no token, no timeout
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the `ArgumentList` launchers in
  `DependencyVulnerabilitySensor` and `GitleaksSecurityScanner` and in the configured-command
  finding recorded in `docs/operations/security/`.
