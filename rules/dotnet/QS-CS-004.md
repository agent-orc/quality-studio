---
id: QS-CS-004
title: Structure tests around observable behavior and real boundaries
language: csharp-dotnet
severity: medium
autofixable: false
version: 1.0.0
status: active
kinds: [code]
levels: [file, module, project]
applies-to: [.cs]
references: [tests/AgentOrchestrator.CodeQuality.Tests/ReviewRunnerTests.cs, tests/AgentOrchestrator.CodeQuality.Tests/InputResolverTests.cs, Agent Studio backend.Tests]
deterministic-check: none
---

## Statement

Name tests by observable behavior, arrange only the boundary state the behavior needs, and assert outputs plus durable side effects. Cover success, rejection, unavailable dependency, cancellation, and concurrency paths where those states exist. Keep fixtures isolated and deterministic.

## Rationale

Quality Studio tests use temporary repositories and verify prompts, metadata, fingerprints, sensor provenance, and failure states. Agent Studio's backend tests similarly pin lifecycle policies and API contracts. Behavior-focused tests survive refactoring while catching contract regressions.

## Bad example

```csharp
[Fact]
public void CallsHelper()
{
    service.Run();
    helper.Verify(value => value.Call());
}
```

## Good example

```csharp
[Fact]
public async Task ReviewAsync_PersistsStableRuleIdAndFingerprint()
{
    var result = await service.ReviewAsync(request, TestContext.Current.CancellationToken);
    Assert.Equal("QS-CS-003", result.Finding.RuleId);
    Assert.StartsWith("sha256:", result.Finding.Fingerprint, StringComparison.Ordinal);
}
```

## Change history

- 2026-08-12, v1.0.0: Initial .NET test-structure rule.
