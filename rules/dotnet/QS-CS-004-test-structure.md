---
id: QS-CS-004
version: 1.1.0
title: Structure tests as isolated Arrange-Act-Assert with behavior-focused names
technology: dotnet
kinds: [code]
category: test-structure
severity: low
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: testing-confidence
since: 1.0.0
---

## Statement

A test method name describes the observable behavior being verified (e.g.
`Project_input_overrides_global_by_id`), not the method under test. Each test arranges its own
isolated fixture (a fresh temp directory, in-memory store, etc.), performs one action, and
asserts on outcomes — it does not depend on state left behind by another test or on execution
order. Dispose owned resources (implement `IDisposable` when a fixture creates files/directories).

## Rationale

`InputResolverTests` creates a unique temp directory per test instance and implements
`IDisposable` to clean it up, and every test method name states the behavior under test rather
than just naming the method it calls. This is what makes the suite safe to run in parallel and
lets a failing test name alone tell a reader what broke, without opening the test body first.

## Detection

Check that the test class owns its fixture (a per-instance temp path, its own store) and disposes it, that the method name states a behavior rather than a member name, and that no test reads or writes state another test created. Shared immutable fixture data is not a violation.

## Good example

```csharp
public sealed class InputResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-input-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Project_input_overrides_global_by_id()
    {
        Write(global, "rules.md", "rules", "all", "all", 10, "global body");
        Write(Project, "rules.md", "rules", "code", "file", 1, "project body");

        var result = new InputResolver().Resolve(root, "code", ReviewLevel.File, global);

        Assert.Equal("project body", Assert.Single(result.Inputs).Content);
    }
}
```

## Bad example

```csharp
[Fact]
public void Test1() // name describes nothing; shares a hard-coded path with other tests
{
    Directory.CreateDirectory("/tmp/shared-fixture");
    var resolver = new InputResolver();
    // ... asserts three unrelated behaviors in one test, no cleanup
}
```

## Change history

- 1.1.0 (2026-09-06): Declared the applicable review kinds and added detection guidance for the generated catalogue.
- 1.0.0 (2026-08-27): Initial rule, grounded in the fixture-isolation and naming conventions
  already used in `backend/tests/AgentOrchestrator.CodeQuality.Tests/InputResolverTests.cs`.