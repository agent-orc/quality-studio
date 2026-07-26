# Quality report

Generated 2026-07-25T11:09:57.7174479+00:00.

## QS-36

**Score: 87/100 (B) · Coverage: 1.87% (2/107 files)**

| Kind / level | Score | Grade | Reviews |
| --- | ---: | :---: | ---: |
| code | 87 | B | 2 |
| ↳ file | 87 | B | 2 |
| security | — | not-reviewed | 0 |
| performance | — | not-reviewed | 0 |

### Findings

Total: 7. Severity: critical 0, high 0, medium 4, low 3, info 0. State: open 7, accepted 0, waived 0, false-positive 0, resolved 0.

- [medium/open] Broad exception types are mapped to unrelated domain errors — src/QualityStudio.Api/Program.cs:45
- [medium/open] Raw exception messages are exposed to API clients — src/QualityStudio.Api/Program.cs:61
- [medium/open] File endpoint loads files into memory without a size limit — src/QualityStudio.Api/Program.cs:148
- [medium/open] Security finding mapping dereferences a nullable range — src/QualityStudio.Api/Program.cs:473
- [low/open] Line-ending detection labels unsupported cases as LF — src/QualityStudio.Api/Program.cs:178
- [low/open] Endpoint workflows are embedded as private static methods in Program — src/QualityStudio.Api/Program.cs:116
- [low/open] HttpClient is registered directly as a singleton — src/QualityStudio.Api/Program.cs:28

### Staleness

Fresh 0, stale 2, policy drift 0, missing 319.

### Sensor posture

- gitleaks 8.24.2: availability not probed
- dependencies 1.0.0: availability not probed

### Trend

- code: 98 (bc99e8c34491, 2026-07-11) → 87 (49d892023caa, 2026-07-21)
- security: no committed review history
- performance: no committed review history
