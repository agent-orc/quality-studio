# SARIF correction: independent API verification

2026-09-19 around 21:48 UTC. Existing freshly built Release API copied to a unique temporary directory; no builds or product source edits. Root's test lanes ran concurrently; these are response-contract checks with no performance budget. All requests reached the real copied API with synthetic local fixtures and isolated registry/data roots.

**16 of 16 expected responses passed; no ambiguous responses.** All 14 earlier cases were repeated, with two additional positive controls.

| Cases | Before | After |
| --- | --- | --- |
| results:{} through SARIF, Roslyn and ESLint (3 cases) | available / 0 findings | unavailable with structure explanation |
| failed invocation, metadata-only run, external result references, zero runs (4 cases) | available / 0 findings | unavailable with specific explanation |
| Missing SARIF report and bare {} (2 cases) | unavailable | unchanged |
| Valid complete inline SARIF with named driver and results:[] | available / 0 findings | unchanged |
| Four coverage fixtures: missing, unrelated XML, empty Cobertura, known Cobertura | available, with missing-report reason only | unchanged |
| New positive: executionSuccessful:true with exitCode:1 | not previously tested | available / 0 findings |
| New positive: valid inline warning diagnostic | not previously tested | available / 1 finding |

Seven formerly accepted incomplete/invalid SARIF cases now fail closed; seven baseline cases stayed stable. The successful invocation control prevents interpreting every nonzero producer code as an execution failure. It uses report-only import; no failing producer command was launched.

The remaining coverage-status note is unchanged. The projection remains unknown/null for absent measurements; no false 100% claim was made or demonstrated.

The copied API process tree was terminated and its resolved, verified temporary directory removed. Runtime hashes, actual responses and the comparison assertions are retained below.

- [After responses and runtime hash](probe-results.json)
- [Machine-readable before/after comparison](comparison.json)
- [Real API log](api-probe.log)
- [Reproducible after probe](../probes-after.py)
- [Before evidence and schema analysis](../README.md)
