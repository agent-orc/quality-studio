# Documentation index

Where to start with the Quality Studio documentation. The product overview and the build and test
lanes are in the [repository README](../../README.md); the change history is in
[`CHANGELOG.md`](../../CHANGELOG.md).

## Running and operating

- [Running Quality Studio](../deployment.md) — run command, environment variables, container host.
- [The data root](../data-root.md) — where runs, reports, rule-pool state and audit trails live.
- [Quality Studio API](../api.md) — endpoints, configuration and curl examples.

## Reviews and their inputs

- [Review inputs](../review-inputs.md) — `.quality/inputs/` guidelines, overrides and the size budget.
- [Rule pool management](../rule-pool-management.md) — rule overrides, custom rules, rule packs,
  per-project applicability, rule-set import and export, and the audit trail.
- [Rule library](../../rules/README.md) — the authored built-in rules and their format.
- [Quality domains and project properties](../quality-domains-and-project-properties.md)
- [UI review runs](../review-runs.md), [change-set reviews](../change-reviews.md),
  [deep flow reviews](../deep-flow-reviews.md)
- [Finding identity and lifecycle](../finding-lifecycle.md)
- [Quality reports](../quality-reports.md)

## Sensors and evidence

- [Deterministic analyzer evidence](../deterministic-analyzer-evidence.md)
- [Gitleaks security scanning](../security-gitleaks.md),
  [security sensor and agent combination](../security-review-combination.md),
  [dependency vulnerability sensor](../dependency-vulnerability-sensor.md)
- [Coverage and risk](../coverage-and-risk.md), [attack coverage matrix](../attack-coverage.md)
- [Derived boundary inventory](../boundary-inventory.md),
  [architecture and typography checks](../architecture-checks.md)

## Structure and contracts

- [Quality Studio concept and contracts v1](../concept.md)
- [Hierarchy derivation](../hierarchy-derivation.md), [hierarchy aggregation](../hierarchy-aggregation.md)
- [Shared schemas](../../schemas/README.md)
- [Backend layout validation](../backend-layout-validation.md),
  [frontend structure and shared styles](../frontend-style-refactoring.md)
- [Model catalog integration](../model-catalog-integration.md),
  [review usage telemetry](../usage-telemetry.md)
