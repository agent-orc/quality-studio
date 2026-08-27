# Quality Studio performance record

## QS-5 hierarchy scan budget

Measured 2026-07-22 on Linux 6.8, .NET 10.0.9, Intel Core i7-8700
(12 logical CPUs), 62 GiB RAM. The corpus was a generic repository containing
5,000 one-line files in one source directory. The command used the Debug build:

```text
dotnet run --project src/quality/quality.csproj --no-build -- scan <fixture>
event=quality.scan.completed projects=1 modules=1 elapsedMs=165
```

The 165 ms result includes hierarchy derivation and review-meta discovery, but
not fixture creation. A regression test independently asserts that all 5,000
files are present and that hierarchy derivation completes within 5 seconds on
the test host. Warm API requests reuse the snapshot while the Git state is
unchanged.

## QS-54 real repository switching

Measured 2026-08-08 on Linux 6.8, .NET 10.0.301, Intel Core i7-8700
(12 logical CPUs). Unlike the QS-40 browser fixture, these requests used the
real API against two existing repositories and included hierarchy derivation,
Git state, review-meta discovery, and the project projection.

| Repository | Tracked files | State | Git status | Hierarchy scan | Review-meta discovery | Projection | Total |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| quality-studio (`0d03986`) | 135 | cold, before | 21.58 ms | 743.20 ms | 9.01 ms | 99.25 ms | 874.90 ms |
| quality-studio (`0d03986`) | 135 | warm, before | 5.49 ms | 0 ms | 0 ms | 0 ms | 5.52 ms |
| agent-taskboard (`32bf8983`) | 3,450 | cold, before | 45.78 ms | 8,272.84 ms | 45.25 ms | 1,418.01 ms | 9,781.93 ms |
| agent-taskboard (`32bf8983`) | 3,450 | warm, before | 17.57 ms | 0 ms | 0 ms | 0 ms | 17.60 ms |
| quality-studio (`0d03986`) | 135 | cold prewarm, after | 36.16 ms | 970.54 ms | 12.51 ms | 176.90 ms | 1,198.15 ms |
| quality-studio (`0d03986`) | 135 | operator request after prewarm | 5.23 ms | 0 ms | 0 ms | 0 ms | 5.30 ms |
| agent-taskboard (`32bf8983`) | 3,450 | cold prewarm, after | 34.00 ms | 9,773.52 ms | 46.81 ms | 1,312.63 ms | 11,167.07 ms |
| agent-taskboard (`32bf8983`) | 3,450 | operator request after prewarm | 17.30 ms | 0 ms | 0 ms | 0 ms | 18.00 ms |

The hierarchy scan is the dominant cold phase: 85% of quality-studio's cold
request and 84.6% of agent-taskboard's. Projection is the second largest block
for agent-taskboard at 14.5%. The smallest measured intervention is therefore
to populate the existing immutable hierarchy and projection caches in a
background hosted service for every registered repository. It deliberately
does not add a second cache or weaken Git-state invalidation. Cold work remains
visible in `qs.repository.prewarm`, but it is removed from the operator's
switch request.

The complete warm switch fan-out exposed a separate ancillary cost:
agent-taskboard's guideline/trace response took 3,169.8 ms while the warm
project response took 65.8 ms. Scan, input, guideline, risk, review-run, and
usage projections now refresh after the dashboard and tree are usable. A real
project-plus-tree run for agent-taskboard measured 219.5–295.8 ms after prewarm.

The API emits a stable JSON event named `qs.repository.switch.backend` with
`repositoryId`, `cache`, `durationMs`, `fileCount`, and a `phases` object
containing `gitStatusMs`, `cacheWaitMs`, `scanMs`,
`reviewMetaDiscoveryMs`, and `projectionMs`. The same phases are exposed in
the standard `Server-Timing` response header. Background measurements use the
same shape in `qs.repository.prewarm`.

The browser contract is `< 100 ms` to a visible transition and `< 500 ms` to a
usable dashboard and tree. The 500 ms bound gives measured headroom above the
295.8 ms real large-repository run while remaining far below the previous
multi-second path. See `frontend/PERF.md` for the reproducible browser harness.

## QS-78 persisted cross-restart snapshot (dossier slice P2)

QS-54's prewarmer removed cold scan/projection work from an operator's request
*within a running process*, but every fresh `dotnet` process still paid the
full scan, projection, and sensor-availability probe again — the QS-59
performance dossier's decision-in-one-page table records this as "initial
usable state ... fails" at 9.584 s median for the real 3,927-file repository.
This slice adds a small on-disk store (`RepositorySnapshotStore`, under
`.quality-studio/cache/repositories/<repoId>.json` in the API's own content
root, never inside a registered repository) that persists the verified
hierarchy snapshot, dashboard projection, and sensor-availability results, and
restores them on the next process start when the repository's Git state and
the registry entry are byte-identical (`RepositoryCacheState` hashes HEAD,
index, dirty/untracked content, global inputs, and the full registration —
id, root, sensors, budgets — into one fingerprint; any change forces a cold
rebuild, so this never weakens the existing correctness invalidation).
`RepositorySensorAvailabilityCache` applies the same repo-state + registry-entry
key to the sensor availability probes that `GET /api/repos/{id}/sensors`
performs on every switch, which is the "sensor init" half of this slice.

Measured 2026-08-27 on Linux 6.8, .NET 10.0.301 (Release), Node 22.23.1,
against the real Agent Studio repository (`/home/agent/runner-work/PROJ-002/repo`,
3,927 tracked files, HEAD `9af1a848`). Reproduce with
`node scripts/measure-project-switch-cache.mjs`; raw output is
`results/agent-studio-switch-cache.json`.

| Scenario | Process → health | Process → project+tree | Background prewarm scan+projection+sensor-init |
| --- | ---: | ---: | ---: |
| Cold process, no persisted snapshot (today's every-restart cost) | 515.90 ms | 5,609.95 ms | 6,818.03 ms (scan 4,396.69 + projection 857.81 + sensor-init 1,298.52) |
| Same repository, next process restart, with a valid persisted snapshot from the run above | 812.29 ms | 1,689.40 ms | 122.95 ms (scan 0 + projection 0.01 + sensor-init 0; only the git-status/registry-fingerprint recheck remains: 26.99 + 93.53 ms) |

A process restart against a previously-visited, unchanged repository goes from
5,609.95 ms to 1,689.40 ms to become usable (3.3x), and the background work the
prewarmer would otherwise redo drops from 6,818.03 ms to 122.95 ms (55.5x),
because scan and sensor-init are now exactly 0 ms on the restored path. The
in-memory warm-switch path (already-running process, repeat request) is
unaffected and remains 234.82–742.46 ms median 468.56 ms in this run, gated by
the known separate 29 MB recursive tree payload (dossier slice P1, not part of
this delivery). `RepositorySnapshotCacheTests` covers restore, and invalidation
on a changed registry entry, a dirty working tree, and a new HEAD; a corrupt
persisted file falls back to a cold rebuild rather than serving stale or
crashing. `ApiSmokeTests.Project_reuses_the_scan_and_projection_caches_on_a_warm_repeat_switch`
guards the existing in-memory warm path at the HTTP level.
