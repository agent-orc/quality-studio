# Backend layout validation

Validated on Windows on 12 September 2026.

## Scope

The three .NET production projects now live in `backend/src/`; their two test
projects and shared fixtures live in `backend/tests/`. The root solution and shared
build properties remain the entry point. Angular retains `frontend/src/` and its
browser tests in `frontend/tests/`; root `tests/` owns cross-repository tooling.

CI, container builds, development and performance launchers, catalogue generation,
source-host repository defaults, fixture paths, and current documentation links use
the new layout. Published historical review JSON is preserved. Its identity test
reconstructs the explicitly migrated source layout in a temporary fixture while
continuing to require stable IDs for unchanged source paths.

## Final checks after integrating the incoming test infrastructure

The final merged tree uses the named lanes from `scripts/test-lanes.mjs`. Each runner
inventoried both relocated test projects before execution; no selection was empty.

| Lane | Core | API | Result |
| --- | --- | --- | --- |
| Portable | 388 passed | 44 passed | Passed without skips |
| Tool-bound | 43 passed, 3 Windows/POSIX skips | 116 passed | Passed |
| Combined non-machine coverage | 431 passed, 3 Windows/POSIX skips | 160 passed | Passed |

The combined lane therefore passed **591 tests**, with the **3 existing operating-system
skips**. External-live and machine-bound cases retain their explicit canary lanes.
No selection or assertion was weakened to pass the merge.

Reproduce from the repository root:

```sh
dotnet build QualityStudio.slnx --configuration Release
node scripts/run-dotnet-lane.mjs portable --configuration Release --no-build
node scripts/run-dotnet-lane.mjs tool-bound --configuration Release --no-build
node scripts/run-dotnet-lane.mjs non-machine --configuration Release --no-build --coverage
dotnet format QualityStudio.slnx --verify-no-changes --no-restore
```

- Release build, format verification, and `git diff --check`: passed.
- Cobertura baseline checks: Core **82.70%** against baseline **79.07%**; API **48.62%** against **43.26%**. Both passed without lowering floors.
- Repository Node checks: **18 passed**, covering the API's complete-repository default, Windows-safe launcher startup/teardown, named-lane and fixture contracts, release-canary retention, and rule-catalogue generation.
- Rule catalogue 1.3.0: matches all 28 authored rules.

The incoming `GitTestRepository` now belongs to `backend/tests/TestSupport/`, while
the Node process fixture remains under root `tests/TestSupport/`. The source contract
inspects actual backend test files and excludes generated `bin`, `obj`, and `TestResults`
directories. The new Git-backed review fixture uses the shared attribute-aware cleanup;
the launcher fixture waits for its own process tree to close before asserting port release.

## Historical machine-bound measurement

Before integrating the named-lane infrastructure, the full API suite measured
`ProjectDashboardTests.Cached_dashboard_for_5000_file_repository_is_within_interaction_budget`
at **201 ms against its 150 ms first-visible budget** on this machine. That previously
recorded observation is separate from the final merged-tree lane receipts above.
Its existing `MachineBound` classification, performance threshold, and canary coverage
remain unchanged.

The [existing test-baseline evidence](operations/test-baseline/index.html) records the
same dashboard check at 354 ms. These observations come from different machine
conditions and do not establish a performance improvement from this refactoring.
