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

## Checks

- `dotnet format QualityStudio.slnx --verify-no-changes --no-restore`: passed.
- `dotnet test QualityStudio.slnx --configuration Release --filter "Category!=MachineBound" --collect:"XPlat Code Coverage"`: passed using the routine CI filter.
- Both .NET Cobertura reports pass `tests/coverage-baseline.json` thresholds.
- Launcher tests: 6 passed, including a check that the relocated API's default and allowed roots resolve to the complete repository containing the solution and Angular workspace.
- Rule-catalogue tests and rule/model catalogue integrity checks: passed.

Final routine-suite results:

```text
Passed!  - Failed:     0, Passed:   158, Skipped:     0, Total:   158, Duration: 1 m 29 s - QualityStudio.Api.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   430, Skipped:     4, Total:   434, Duration: 1 m 45 s - AgentOrchestrator.CodeQuality.Tests.dll (net10.0)
```

## Separate machine-bound measurement

The full, unfiltered API suite passed all functional checks. Its existing
`ProjectDashboardTests.Cached_dashboard_for_5000_file_repository_is_within_interaction_budget`
test measured **201 ms against its 150 ms first-visible budget** on this machine.
That test carries the existing `MachineBound` category and is excluded by the normal
CI filter; its release-canary coverage remains unchanged. No performance threshold
or classification was relaxed.

The [existing test-baseline evidence](operations/test-baseline/index.html) already
records this same dashboard check at 354 ms. These are observations under different
machine conditions, not a claim that the refactoring improved its performance.
