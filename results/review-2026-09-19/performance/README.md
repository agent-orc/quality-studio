# Real-API repository-switch validation

The unchanged frontend/tests/project-switch-perf.mjs harness passed on 2026-09-19T21:11:22Z against the current Release QualityStudio.Api DLL, Edge 153.0.4234.32, and 1,600 generated source files (1,603 total repository files). Requests reached a real ephemeral API through the Angular proxy; there was no Playwright response interception.

QUALITY_STUDIO_DATA_ROOT was isolated at results/review-2026-09-19/performance/isolated-data. The harness generated its own temporary Git repositories and registry, selected free localhost ports, and completed its browser/API/web shutdown plus temporary fixture cleanup. The original app database and registered repositories were not changed.

| Measurement | Three switches | Budget |
| --- | --- | --- |
| Transition visible | 20.6 / 15.8 / 12.7 ms | each < 100 ms |
| Repository usable | 91.4 / 76.6 / 222.5 ms | each < 500 ms |
| First interactive realistic dashboard | 39.6 ms | < 150 ms |

Cold prewarm: 2,918.27 ms. This was completed before the interaction measurements as specified by the existing harness.

Concurrent .NET test work ran in the background, so these are developer-machine observations under background load, not isolated lab benchmarks. Every acceptance budget passed; no repeat was necessary. Runtime was Node 24.18.0, whereas repository/CI tooling pins Node 22; a subsequent complete coverage suite and production build passed under pinned Node 22.23.1; see ../node22/README.md.

Both light/dark screenshots were visually inspected. The light capture caught the intended Updating transition with retained dashboard data; the dark capture shows the completed dashboard. Repository name, 1,603-file count, health tiles and explorer remain coherent. Missing review/security evidence is labelled as missing/unavailable. The real API also exposes locally cached provider quota chips; those chips are machine state, not fixture-generated quota assertions.

Evidence: project-switch-perf.json, project-switch-run.log, project-switch-transition-light.png, project-switch-transition-dark.png. No product changes were made for this validation.
