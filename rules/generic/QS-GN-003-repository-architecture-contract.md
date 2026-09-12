---
id: QS-GN-003
version: 1.0.0
title: Preserve the repository's declared architecture
technology: generic
kinds: [code]
category: maintainability
severity: medium
defaultOn: true
autofixable: false
deterministicRuleIds: [architecture/missing-directory, architecture/forbidden-source-path, architecture/unexpected-entry, architecture/invalid-contract]
relatedGuideline: generic-code
since: 1.3.0
---

## Statement

Respect the repository's declared source ownership and dependency direction. When a
`quality-architecture.json` contract exists, keep required directories, retired source
locations and directory allowlists consistent with it. Treat an intentional architecture
change as a coordinated update to code, contract, scripts and documentation.

## Rationale

An asymmetric or drifting repository layout makes ownership harder to discover and leaves
build, test and review tools referring to stale locations. A version-controlled contract
turns an agreed architecture into reviewable evidence without assuming one folder convention
is correct for every repository.

## Detection

Use the architecture sensor's source-located findings and compare touched paths to the
repository-owned contract. Flag source returned to a retired location, missing required
directories, unexpected direct children and invalid contracts. Do not infer violations
from folder names alone when a repository has no contract. Generated build output and
historical review metadata are not product source. Directory checks do not prove component
cohesion or dependency direction; language-native analyzers and reviewer judgment cover those.

## Good example

```json
{
  "schemaVersion": 1,
  "requiredDirectories": ["backend", "backend/tests", "frontend/src"],
  "forbiddenSourcePaths": ["src", "backend/src"],
  "directoryRules": []
}
```

## Bad example

```text
quality-architecture.json  # declares backend and forbids retired src locations
backend/Api/Program.cs
backend/src/AnotherApi/Program.cs  # reintroduces an unnecessary retired source wrapper
```

## Change history

- 1.0.0 (2026-09-12): Added repository-owned structure checks and deterministic architecture sensor mappings.
