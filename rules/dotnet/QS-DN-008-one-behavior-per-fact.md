---
id: QS-DN-008
title: Name each [Fact] after the observable behavior it proves
technology: .NET
category: test-structure
kinds: [code]
severity: low
autofixable: false
tier: extended
status: active
since: 2026-08-27
---

## Statement

Give each `[Fact]` a name that states the observable behavior being verified
(`Structural_metrics_match_known_fixture_repository`), and keep one behavior
per test rather than asserting several unrelated outcomes in one method
because they happen to share setup.

## Rationale

A behavior-named test tells a reader what broke from the test name alone in
a CI failure list, before opening the file. `ProjectDashboardTests` follows
this consistently — `Structural_metrics_match_known_fixture_repository`,
`Cached_dashboard_for_5000_file_repository_is_within_interaction_budget` —
each name states a claim the test body then proves. A name like `Test1` or
`DashboardWorks` forces the reader back into the test body (or the failure
stack trace) to find out what actually failed.

## Good example

```csharp
[Fact]
public async Task Structural_metrics_match_known_fixture_repository()
{
    // ...
    Assert.Equal(5, dashboard.Metrics.FileCount);
    Assert.Equal(2, dashboard.Metrics.FolderCount);
}
```

## Bad example

```csharp
[Fact]
public async Task Test1()
{
    // asserts file count, folder count, coverage percent, AND dependency
    // edges in one method — a failure here says nothing about which of the
    // four unrelated claims broke until you read the whole body
}
```
