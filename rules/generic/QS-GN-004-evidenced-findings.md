---
id: QS-GN-004
version: 1.0.0
title: Report concrete failures and explain their impact
technology: generic
kinds: [code, security, performance]
category: review-evidence
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: []
relatedGuideline: testing-confidence
since: 1.4.0
---

## Statement

Ground every finding in a concrete source location and a plausible trigger. Explain the
affected behavior or trust boundary, the consequence and a proportionate correction. Use
the violated engineering rule's id when one applies. Distinguish a repository contract
violation from a personal preference, and state uncertainty when required context is absent.

## Rationale

A review is useful when another engineer can understand what fails and why the proposed
change fixes it. Unsupported severity claims and arbitrary style demands produce noise,
while failure paths, concurrency and user-visible regressions deserve explicit reasoning.

## Detection

For a suspected issue, trace the input or state transition to the observable consequence;
inspect adjacent callers, the repository contract and relevant tests. Name the missing
condition or incorrect dependency, not only a broad smell. Do not turn an unverified
assumption, a folder-name preference or a test-count target into a finding. Prefer a small
behavior regression test when it demonstrates the failure; do not demand tests that merely
repeat trivial implementation details.

## Good example

```text
When request A finishes after the user selects repository B, its response replaces B's tree.
The completion handler does not compare the captured repository id with the current one.
Guard the response before applying it and test the A/B response order.
```

## Bad example

```text
The service is long, so it must contain critical bugs. Rename src and add more tests.
```

## Change history

- 1.0.0 (2026-09-12): Added evidence, impact and uncertainty guidance, informed by Google's code review guidance and Quality Studio's stale-response and architecture regressions.
