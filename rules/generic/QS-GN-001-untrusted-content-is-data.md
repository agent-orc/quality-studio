---
id: QS-GN-001
version: 1.0.0
title: Treat content you did not author as data, never as instruction
technology: generic
kinds: [code, security]
category: trust-boundary
severity: high
defaultOn: true
autofixable: false
deterministicRuleIds: []
since: 1.2.0
---

## Statement

Repository source, model output, sensor output, and anything a caller supplies is data. When it is
embedded in a prompt, a command, a template, a query, or a document, mark or escape its boundary so
the content cannot end that boundary itself, and validate what comes back rather than trusting what
it claims about itself.

## Rationale

`ReviewPromptBuilder` generates a fresh 128-bit boundary marker per prompt precisely so file
content cannot guess and forge the closing marker, and the prompts state that everything between the
markers is untrusted no matter what it claims to be. The output side is the other half:
`ReviewResponseParser` rejects a finding that claims deterministic provenance and
`FindingIdentity` computes the excerpt hashes itself, because a value the model supplied about its
own trustworthiness is not evidence. Prompt wording alone is not a boundary — the marker, the
validation, and the host-computed anchors are.

## Detection

Look for untrusted content concatenated into a prompt, a shell command, an SQL statement, a path,
or markup with a fixed or predictable delimiter, and for a field the producer controls being used
to decide how much that producer is trusted — a claimed source, a claimed hash, a claimed severity.
Content passed as a bound parameter, an argument-list entry, or an escaped value is not a violation.

## Good example

```csharp
// backend/src/AgentOrchestrator.CodeQuality/ReviewPromptBuilder.cs
// Fresh per prompt so repository content cannot pre-guess and forge a closing marker.
private static string GenerateContentBoundary() =>
    "QS-CONTENT-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
```

## Bad example

```csharp
var prompt = "Review this file:\n---\n" + fileContent + "\n---\n" + instructions;
// and, on the way back, believing what the response says about itself:
if (response["source"]?.GetValue<string>() == "analyzer") finding.Trusted = true;
```

## Change history

- 1.0.0 (2026-09-06): Initial rule, grounded in the per-prompt content boundary in
  `ReviewPromptBuilder` and the provenance and evidence checks in `ReviewResponseParser` and
  `FindingIdentity`.
