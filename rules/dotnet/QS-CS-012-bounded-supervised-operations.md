---
id: QS-CS-012
version: 1.0.0
title: Give every external operation a timeout and keep it off the shared reader
technology: dotnet
kinds: [performance]
category: bounded-execution
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

An operation that waits on a process, a network call, or an agent runs under an explicit wall-clock
timeout as well as its `CancellationToken`. A queue's reader starts such work and supervises it; it
does not await it to completion, so one operation that never returns cannot stop every operation
behind it.

## Rationale

`ReviewJobService.ExecuteAsync` is a single-reader channel that awaits each run, so an operation
that never returns parks the reader for good, and cancelling it makes the durable state terminal
without freeing the reader — the queue is then permanently stopped while looking healthy. The
boundary sensor is the demonstration: 1.4 s on 144 files, and no return at all within a 300-second
client timeout on a 1,269-file frontend.

## Detection

Look for `await`s on a process, HTTP, or agent call with no linked timeout token, and for a channel
or queue reader whose loop body awaits the whole operation. A timeout that only exists on the
client is not one; the bound has to be on the side that holds the resource.

## Good example

```csharp
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(OperationTimeout);
var run = Task.Run(() => sensor.ScanAsync(root, timeout.Token), timeout.Token);
_supervisor.Track(operationId, run);   // the reader keeps draining the queue
```

## Bad example

```csharp
await foreach (var job in reader.ReadAllAsync(stoppingToken))
{
    // One non-returning operation parks the only reader; nothing behind it is ever picked up.
    await runner.RunAsync(job, stoppingToken);
}
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the single-reader review job channel and the
  boundary-sensor timeout recorded in `docs/operations/security-concept/`.
