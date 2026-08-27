---
id: QS-CS-004
title: Name tests by behavior; parameterize with Theory
summary: xUnit tests should be named Subject_condition_expectedBehavior, use [Theory]/[InlineData] for parameterized cases, and propagate the test's own cancellation token.
technology: dotnet
category: test-structure
severity: low
autofixable: false
defaultOn: false
kinds: [code]
levels: [file, function]
version: 1.0.0
status: active
---
## Statement

Test method names MUST read as `Subject_condition_expectedBehavior` (the
class/method under test, the scenario, then the outcome), so a failing test
name alone tells you what broke. Repeated test bodies that only differ by
input/expected-output MUST be collapsed into one `[Theory]` with
`[InlineData]` rows rather than copy-pasted `[Fact]`s. Async tests that call
into HTTP/process APIs MUST pass the test's own cancellation token, not
`CancellationToken.None`, so a hung dependency fails the test instead of the
whole run.

## Rationale

A `[Fact]` named `Test1` or `ReturnsCorrectResult` forces whoever reads a red
CI run to open the method body before they know what failed. Copy-pasted
near-identical `[Fact]`s multiply the cost of changing the behavior they all
exercise, since each copy has to be updated. `CancellationToken.None` inside
a test means a genuinely hung dependency times out the whole test run
instead of failing that one test with a clear signal.

## Good example

`tests/QualityStudio.Api.Tests/ApiSmokeTests.cs:22`:

```csharp
[Fact]
public async Task Tree_returns_derived_hierarchy_and_kind_states()
{
    using var client = application!.CreateClient();
    using var response = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);
    ...
}
```

`tests/AgentOrchestrator.CodeQuality.Tests/QualityFindingContractTests.cs:12`:

```csharp
[Theory]
[InlineData("quality-finding.source-located.v1.json", "standing-unit", 1)]
[InlineData("quality-finding.task.v1.json", "task-change", 0)]
public void Fixtures_validate_and_round_trip(string fixture, string subjectType, int locationCount)
```

## Bad example

```csharp
[Fact]
public async Task Test1()
{
    var response = await client.GetAsync("/api/tree?path=", CancellationToken.None);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task Test2()
{
    var response = await client.GetAsync("/api/tree?path=src", CancellationToken.None);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

Two near-identical facts, opaque names, and a hardcoded `CancellationToken.None`
that lets a hung HTTP call stall the whole run instead of failing fast.

## Changelog

- 1.0.0 (2026-08-27): Initial rule, grounded in Quality Studio's own test
  suite during the QS-90 rule-library seeding session.
