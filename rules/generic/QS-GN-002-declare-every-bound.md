---
id: QS-GN-002
version: 1.0.0
title: Give every resource an explicit bound
technology: generic
kinds: [code, security, performance]
category: resource-limits
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
since: 1.2.0
---

## Statement

Anything whose size someone else decides carries a stated limit: request and document bytes, nesting
depth, collection and result counts, retained cache entries, concurrent operations, and wall-clock
duration. The limit is validated where it is configured and enforced where the resource is consumed,
and exceeding it is a clear refusal rather than a slow failure.

## Rationale

`ApiSecurity` validates its limits at start-up — request bodies between 1 KiB and 10 MiB, 1 to 1024
concurrent requests, 1 to 1000 spend requests per minute — so a misconfiguration fails the host
instead of quietly removing a bound. The gaps show what happens without that discipline: reading a
repository file with no size check lets one caller exhaust process memory by repeating a request,
and the sidecar index retains a parsed document per file with no byte cap, so cost grows with a
number the repository chooses rather than one this process agreed to.

## Detection

For each resource crossing a boundary, ask what its limit is and where it is enforced: a read with
no length check, a parse with no depth bound, a collection accumulated in a loop with no cap, a
cache with no eviction, a wait with no timeout, a retry with no ceiling. A limit only present in
documentation or in the client is not a bound.

## Good example

```csharp
// backend/QualityStudio.Api/ApiSecurity.cs
if (MaxRequestBodyBytes is < 1024 or > 10 * 1024 * 1024)
    throw new InvalidOperationException("MaxRequestBodyBytes must be between 1 KiB and 10 MiB.");
if (MaxConcurrentRequests is < 1 or > 1024)
    throw new InvalidOperationException("MaxConcurrentRequests must be between 1 and 1024.");
```

## Bad example

```csharp
// Size chosen by the repository, retained for the life of the process, parsed at default depth.
var payload = JsonDocument.Parse(File.ReadAllText(sidecarPath));
index[relativePath] = payload.RootElement.Clone();
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the start-up-validated limits in `ApiSecurity`
  and the unbounded file read and sidecar index recorded in `docs/operations/security/`.
