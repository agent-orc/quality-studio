# Release API repository-switch validation after Git hardening

The unchanged frontend/tests/project-switch-perf.mjs harness passed with exit 0 on 2026-09-19T21:50:52Z, after the Git/fsmonitor hardening build and before the final npx command-runner extension. It used that Release API, Node 24.18.0 and Edge 153.0.4234.32, 1,600 generated source files (1,603 total repository files), and real API requests through the Angular proxy. No Playwright API interception was used.

| Measurement | Three switches | Acceptance budget |
| --- | --- | --- |
| Transition visible | 18.9 / 18.0 / 14.5 ms | each < 100 ms |
| Repository usable | 93.3 / 192.5 / 177.9 ms | each < 500 ms |
| First interactive realistic dashboard | 36.9 ms | < 150 ms |

Cold prewarm took 3,263.15 ms before the measured interactions. Root's .NET coverage lane ran concurrently; these are developer-machine observations under background load. Every interaction budget passed, so no repeat was needed.

The previous run is preserved in ../performance. That earlier run preceded the Git/fsmonitor hardening; this run validates the Release build after that hardening. The npx command-runner extension was added later, so these measurements and recorded DLL hashes do not identify the final end-of-review binaries. Runtime remains Node 24 for comparable performance conditions; the complete frontend coverage suite and production build were separately validated under pinned Node 22.23.1 (../node22/README.md).

Both screenshots were visually inspected. The light capture shows the expected Updating state with retained dashboard data; the dark capture shows the completed dashboard. Repository title, 1,603-file count, explorer and health cards agree. Missing review/security/test-coverage evidence is labelled as missing or unavailable. Provider quota chips reflect local machine cache, not generated fixture assertions.

The harness used its own temporary repositories, registry and ephemeral localhost ports, then completed browser/API/frontend shutdown and temporary-fixture cleanup. Generated app data is isolated in this directory's isolated-data subdirectory and excluded by the review results .gitignore. No original app database or registered repository was modified. No product source changes or builds were performed for this validation.

Evidence: project-switch-perf.json, project-switch-run.log, runtime.log, release-hashes.json, project-switch-transition-light.png and project-switch-transition-dark.png.
