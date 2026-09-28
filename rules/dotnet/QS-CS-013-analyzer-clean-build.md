---
id: QS-CS-013
version: 1.0.0
title: Keep the build free of compiler and analyzer diagnostics
technology: dotnet
kinds: [code]
category: static-analysis
severity: medium
defaultOn: false
autofixable: false
deterministicRuleIds: [CS*, CA*, IDE*]
relatedGuideline: dotnet-api-safety
since: 1.6.0
---

## Statement

Keep every project free of the compiler and Roslyn analyzer diagnostics the repository enables.
Fix a reported diagnostic at its cause. Suppress one only in source (`#pragma warning disable`
with a matching restore, or `[SuppressMessage]` with a `Justification`) when the analyzer is
wrong for that exact site, never by lowering the analysis level for a whole project.

## Rationale

A build that is warning-clean turns every new diagnostic into a visible change instead of noise
in a long list nobody reads. Quality Studio's Roslyn sensor reads the diagnostics from a forced
build and honours in-source suppressions, so a justified suppression stays out of the findings
while its count remains visible, and a silenced project cannot pass for a clean one.

## Detection

Use the Roslyn sensor's findings for the reviewed files: each carries the compiler (`CS`),
analyzer (`CA`) or code-style (`IDE`) id and its location. Explain what the diagnostic protects
against at that site and the proportionate fix. Diagnostics that a more specific rule claims,
such as `CA2016` under QS-CS-003, are explained under that rule. A suppression without a
justification, or a project-wide `NoWarn` that hides a diagnostic class, is a finding; a single
justified in-source suppression is not.

## Good example

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

## Bad example

```xml
<PropertyGroup>
  <!-- Silences the analyzers instead of fixing what they found. -->
  <NoWarn>$(NoWarn);CA1822;CA2016;CS8618</NoWarn>
</PropertyGroup>
```

## Change history

- 1.0.0 (2026-09-28): Initial opt-in rule that owns the compiler, analyzer and code-style diagnostic families no more specific rule claims.
