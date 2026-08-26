# Test lanes and host boundaries

The required pull-request contract has two lanes. The portable product lane runs on
Linux with pinned .NET 10.0.301, Node 22.23.1, Playwright Chromium, Git, and Gitleaks
8.24.2. The host-integration lane runs the launcher fixtures independently on Linux
and Windows with dynamically allocated ports.

All test-owned child-process creation is centralized in
`tests/TestSupport/TestToolProcess.cs`. It captures stdout and stderr and reports a
missing tool or non-zero exit as a diagnostic failure. The audited tool boundaries
are:

- Git-backed repository behavior in `ChangeSetReviewTests`, `CoverageSensorTests`,
  `GitleaksSecurityScannerTests`, `QualityReportTests`, `RepositoryHierarchyBuilderTests`,
  `StalenessEvaluatorTests`, `AgentStudioImportTests`, `ApiSecurityTests`,
  `ApiSmokeTests`, and `ProjectDashboardTests`.
- The fake Gitleaks executable built by `GitleaksSecurityScannerTests`, which uses the
  pinned .NET SDK already required by the product lane.

These are controlled tool integrations, not timing tests. They remain in the required
lane because every executable is explicitly provisioned there. The only
`MachineBound` xUnit checks are the 5,000-file hierarchy timing assertion and the API
dashboard timing assertion; both run only in the release canary.

The repository enforces this declaration with `ProcessBoundaryTaxonomyTests`: adding
an ad-hoc test process or silently changing the machine-bound inventory fails the
required test suite.
