# Quality Studio performance record

## QS-5 hierarchy scan budget

Measured 2026-07-22 on Linux 6.8, .NET 10.0.9, Intel Core i7-8700
(12 logical CPUs), 62 GiB RAM. The corpus was a generic repository containing
5,000 one-line files in one source directory. The command used the Debug build:

```text
dotnet run --project src/quality/quality.csproj --no-build -- scan <fixture>
event=quality.scan.completed projects=1 modules=1 elapsedMs=165
```

The `src/quality` tool used for that measurement was a superseded second CLI
and was removed on 2026-09-06; the same hierarchy derivation now runs inside
`dotnet run --project backend/quality-cli -- scan <fixture>` and behind the API.

The 165 ms result includes hierarchy derivation and review-meta discovery, but
not fixture creation. A regression test independently asserts that all 5,000
files are present and that hierarchy derivation completes within 5 seconds on
the test host. Warm API requests reuse the snapshot while the Git state is
unchanged.

### Re-measurement after the derivation rework (2026-09-06)

The .NET adapter now parses MSBuild project items and C# syntax instead of
matching regular expressions (see `docs/hierarchy-derivation.md`). Measured on
Windows 11, .NET 10.0.301, calling `RepositoryHierarchyBuilder.Build` in process
— no process start, no Git state, no review-meta discovery — five consecutive
runs per process, alternating the pre-change and post-change build so both see
the same machine state. The fixture was measured in two alternating passes, ten
samples per build.

| Corpus | Before | After |
| --- | --- | --- |
| This repository (2 project roots, 7 modules, 161 File units) | 5,932 ms min / 6,682 ms median | 1,942 ms min / 2,860 ms median |
| Generic 5,000-file fixture | 10,992 ms min / 15,109 ms median | 5,204 ms min / 12,380 ms median |

The .NET path got faster because build output is no longer walked, opened, or
parsed: the previous adapter enumerated every `.cs` file below a project
directory including `bin` and `obj`, and read each source twice. Function units
rose from 1,479 to 1,931 for the same 161 files, which is the accuracy change,
not a cost: the regex de-duplicated distinct members by name and missed others.
The generic fixture path is unchanged code, and its numbers confirm that.

These absolute values are not comparable with the 165 ms Linux figure above.
This host ran at 100% CPU from unrelated workloads throughout the session, and
5,000 file opens under Windows real-time scanning dominate the fixture run;
individual runs of identical code varied by a factor of ten. The
`RepositoryHierarchyBuilderTests.GenericFiveThousandFileScanStaysWithinBudget`
regression test passed three of four attempts here and failed once while the
host was saturated. The 5-second budget therefore holds for the derivation
itself but is not robust against an unrelated load on a Windows developer
machine; a saturated host, not the adapter, is what breaks it.

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

## QS-82 lazy tree transport

Re-measured 2026-08-24 on the same 3,927-file Agent Studio repository used by the
QS-59 dossier. The versioned `/api/tree/v2` contract returns one level with
aggregate facts, `hasChildren`, cursor/limit paging, and ETag support. The
legacy recursive endpoint remains available during migration. Both arms of the
comparison run in one warmed API process, so they share JIT and OS cache state.

| Measurement | Recursive v1 | Lazy v2 | Change |
| --- | ---: | ---: | ---: |
| Root payload | 29,119,333 bytes | 15,391 bytes | -99.95% |
| Root request, 10 warm samples | 334.71 ms median / 1,109.25 ms p95 | 32.54 ms / 44.03 ms | -90.28% median |
| Project plus root, 10 warm samples | 331.36 ms median / 902.38 ms p95 | 31.23 ms / 42.45 ms | -90.58% median |
| Cached child page, 10 samples | n/a — descendants were eager | 5.12 ms / 6.58 ms | 304 on a repeat conditional request |
| Real-browser large-repository switch, 5 samples | no equivalent retained QS-59 series | 19.8 ms / 87.0 ms | all v2 samples pass 500 ms |
| Restarted process to usable, 5 samples | QS-59: 10,004.99 ms median | 1,516.87 ms median | -84.84%; clears the 2 s target |

The absolute v1 numbers are host-sensitive — the 2026-08-12 run on a slower host
measured 825.30 ms median for the same v1 root request. The reduction
percentages, measured within a single process per run, are the portable result.

Re-measured on 2026-09-07 after rebasing onto main, against this repository
served by a warmed `QualityStudio.Api` on the Linux review host. The repository
is smaller than the Agent Studio one above, so only the ratio carries over:

| Measurement | Recursive v1 | Lazy v2 | Change |
| --- | ---: | ---: | ---: |
| Root payload | 2,868,549 bytes | 2,255 bytes | -99.92% |
| Root request, 10 warm samples | 79.13 ms median / 93.85 ms p95 | 3.20 ms / 3.55 ms | -95.96% median |
| Cached child page, one level of 5 | n/a — descendants were eager | 4.73 ms | pinned to the root snapshot ETag |
| Repeat conditional root request | — | 9.95 ms | 304 Not Modified |

The same run confirms the response header
`Server-Timing: tree-snapshot;dur=0.08, tree-projection;dur=3.72, tree-serialization;dur=3.16`
and that `/api/tree/v2/search` resolves a file that no expanded level had
loaded.

Tree transport now emits `Server-Timing` phases for snapshot lookup, aggregate
projection, and JSON serialization. The structured `qs.tree.transport` event
adds response bytes and total response-completion time. A one-slot derived
projection per repository reuses the immutable QS-54 hierarchy snapshot, is
populated by that prewarmer, and pins lazy pages to the root snapshot ETag. It
does not duplicate the QS-54 or QS-78 hierarchy caches. Reproduce the backend
distribution with `node scripts/measure-tree-transport.mjs` and the live browser
path with `node scripts/measure-lazy-tree-browser.mjs`.
