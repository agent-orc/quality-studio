# Quality Studio performance record

## QS-95 boundary inventory scaling

Measured 2026-08-26 on the task host with .NET 10.0.301 against the real
Agent Studio checkout at `/home/agent/runner-work/repo`. The sensor found 1,773
eligible files. A 30-second baseline timed out after consuming 30.16 seconds of
CPU. A sample profile identified backtracking in MVC controller discovery;
after that expression alone was made non-backtracking, the full scan still took
40.31 seconds because every server endpoint rebuilt route expressions and
rescanned every JavaScript/TypeScript line for client consumers.

Controller discovery is now non-backtracking. Browser client calls and host
reachability are indexed once per scan. The final real-repository verification
completes in 1.87 seconds (2.08 seconds user CPU), producing 583 boundary
entries and 917 mechanical findings. It honestly reports 1,772 of 1,773 files
analyzed because one 19 MiB HTML artifact exceeds the 2 MiB source limit. A
bounded 100-file page completes in 0.29 seconds and its next incremental page
in 0.27 seconds. All three partial results name their omission reasons, do not
replace repository truth, and exit with code 2.

A regression fixture creates 1,500 source files, 800 server routes, and 1,499
matching client calls. It verifies consumer joins and full coverage under a
10-second host budget, then verifies bounded and continuation-page coverage
metadata.

The rebased delivery was re-verified against the current Agent Studio
`develop` checkout at `/home/agent/promotion/agent-studio` (`c4e305b`). The
larger 3,180-file corpus completed in 5.34 seconds, with 3,179 files analyzed
and the same 19 MiB HTML artifact reported as omitted. Bounded and continuation
pages of 100 files completed in 0.43 and 0.40 seconds respectively, returned
exit code 2, and did not persist an inventory.

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
