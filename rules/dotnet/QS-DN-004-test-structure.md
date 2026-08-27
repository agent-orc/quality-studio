---
id: QS-DN-004
version: 1.0.0
title: Structure tests around observable behavior
language: csharp
kinds: [code]
appliesTo: [**/*.cs]
severity: medium
defaultOn: true
autofixable: false
deterministic: false
---

## Statement

Name tests for observable behavior, arrange only the state relevant to that behavior, exercise public seams, assert the contract and durable side effects, and isolate filesystem state with a unique disposable test directory.

## Rationale

Behavior-focused tests survive refactoring and expose regressions in repository-owned artifacts. Quality Studio's review and sensor tests verify JSON contracts, paths, provenance, and unavailable behavior rather than private implementation calls.

## Bad example

```csharp
[Fact]
public void Test1() {
    Assert.NotNull(typeof(ReviewRunner).GetField("_agent", PrivateFlags));
}
```

## Good example

```csharp
[Fact]
public async Task Review_writes_a_finding_with_the_named_rule_id()
{
    var result = await runner.ReviewAsync(request, TestContext.Current.CancellationToken);
    Assert.Equal("QS-DN-003", ReadFirstFinding(result.MetaPath).RuleId);
}
```

## Change history

- 2026-08-12: Initial .NET seed grounded in Quality Studio's xUnit contract tests.
