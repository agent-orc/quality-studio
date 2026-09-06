---
id: QS-CS-010
version: 1.0.0
title: Key caches on derived state and give every cache a bound
technology: dotnet
kinds: [performance]
category: caching
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: dotnet-api-safety
since: 1.2.0
---

## Statement

A cache key is derived from the content it stands for, so a stale entry is impossible rather than
unlikely — not a timestamp, not a time-to-live. Every cache also declares its bound: a size cap
with eviction, or a key space that provably cannot grow (one entry per registered repository, not
one per observed commit).

## Rationale

`RepositoryHierarchyCache.GetMeasured` keys a slot on the Git state — HEAD, the staged index, and
the hashed contents of every dirty or untracked path — which is why a warm switch is 17.60 ms
against a 9,781.93 ms cold scan without a freshness window to guess at. The bound is the part that
is easy to forget: `ProjectDashboardService` uses the same derived key but keeps one full dashboard
per `(root, gitState)` pair, so every commit and every dirty state ever observed stays in memory
for the life of the process.

## Detection

Look at each `ConcurrentDictionary`, `Dictionary`, or `MemoryCache` field for two things: what the
key is derived from, and what removes an entry. A key containing a timestamp, a run id, or a
monotonically growing value with no eviction is a violation; a key space bounded by a registration
list is not.

## Good example

```csharp
// src/QualityStudio.Api/ProjectDashboard.cs
var key = root + "\0" + snapshot.GitState;   // derived from content, so a stale hit cannot happen
if (cache.TryGetValue(key, out var cached)) return cached;
```

## Bad example

```csharp
// One retained entry per commit and per dirty state, for the life of the process.
private readonly ConcurrentDictionary<string, Dashboard> cache = new();
public Dashboard Get(string root, string gitState) =>
    cache.GetOrAdd(root + "\0" + gitState, _ => Build(root));
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the Git-state keys of `RepositoryHierarchyCache`
  and `ProjectDashboardService` and the unbounded growth of the latter's key space.
