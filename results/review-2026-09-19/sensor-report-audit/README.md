# Additional report-sensor audit (before SARIF follow-up fix)

**Follow-up verified:** the corrected Release API passed all 16 independent response checks; the seven false-available SARIF cases now return unavailable. [After validation and preserved controls](after/README.md).

Read-only source review plus 14 isolated API probes, 2026-09-19 around 21:34 UTC. No product source edits or builds were performed. The probe copied the existing Release API output to its own temporary host, registered only a generated fixture repository, isolated all app data, and called the real sensor endpoint. The copied core DLL SHA256 is recorded in probe-results.json. Its API process tree and verified temporary directory were removed. The first cleanup needed a retry because a short-lived child held the host directory; both attempts' temporary files were removed.

These results capture behavior BEFORE the subsequently assigned SARIF correction. The security_review agent owns that correction and its regression evidence; this note is retained as a baseline.

## P2: SARIF scan completeness is not enforced

[Source: SarifSensor.cs](../../../backend/AgentOrchestrator.CodeQuality/SarifSensor.cs), lines 128, 147-187; [shared Roslyn/ESLint delegation](../../../backend/AgentOrchestrator.CodeQuality/AnalyzerSensors.cs), lines 60-79.

| Synthetic report | Observed result |
| --- | --- |
| Missing file; bare {} | unavailable, zero findings, explicit reason |
| SARIF 2.1.0 with named driver and results:[] | available, zero findings (valid clean control) |
| Same envelope with results:{} | available, zero findings, no reason; reproduced for sarif, roslyn and eslint |
| Same envelope with results:[] and invocations:[{executionSuccessful:false,exitCode:2}] | available, zero findings, no reason |
| Named driver with omitted results (metadata only) | available, zero findings, no reason |
| results:[] plus externalPropertyFileReferences.results with itemCount:1 | available, zero findings, no reason; external results were not resolved |
| runs:[] | available, zero findings, no reason |

The supported import contract should distinguish a complete inline scan from metadata-only, incomplete, failed or unsupported reports. An empty results array must remain valid. A results object is malformed. An empty runs array and omitted results can describe valid SARIF documents without establishing that an actual scan completed. Externalized result files need an explicit supported/unsupported policy; no remote retrieval is needed for a fail-closed contract.

The [official OASIS SARIF 2.1.0 schema](https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json) defines results as an array, distinguishes metadata-only logs from actual scans, and defines executionSuccessful as the invocation outcome. It also permits zero runs and external result references. This is a scan-evidence correctness issue, not proof of code execution, credential disclosure or a hidden vulnerability in a real repository.

The command path at SarifSensor.cs:95-104 checks that a report exists but otherwise discards the process exit status. This is a source-only observation: failing producer commands that still write reports were not executed here. Exit codes require producer-specific interpretation because analyzers can return nonzero when findings exist. A blanket nonzero rejection would be incorrect.

Suggested effort: approximately half to one engineering day for an explicit inline-scan contract, producer outcome policy and focused regressions. Root assigned a scoped follow-up to the security agent.

## P3: Coverage ingestion status is less specific than its projection

[Coverage.cs](../../../backend/AgentOrchestrator.CodeQuality/Coverage.cs): parser dispatch 298-311, sensor status 516-526, projection 83-99 and evidence 125-128.

The real endpoint reports available=true for missing coverage reports (with an explanatory reason), unrelated well-formed XML (<unrelated/>) and empty Cobertura (<coverage/>, both without a reason). A valid one-line Cobertura fixture is the positive control. Coverage findings are always empty because this sensor stores measurements separately; zero findings alone does not indicate a clean test result.

Source inspection confirms that empty/no matching coverage data projects to state unknown and null percentages, and model evidence explicitly says coverage is unknown. No false 100% coverage result was demonstrated. Follow-up: distinguish parser availability from measured coverage and reject unsupported XML roots; approximately half a day including missing/empty/unsupported controls.

## Other inspected paths

TypeScriptAnalyzerSensor captures compiler text itself, supports the pinned profile's path(line,column): error|warning TSnumber diagnostics, and rejects nonzero exits with no parseable diagnostics (AnalyzerSensors.cs:180-186, 259-262). AngularCompilerSensor has the equivalent guard (99-104) and an existing failed-compilation regression. DotNetBuildSensor rejects failed restore and failed build without a parseable error (approximately 91-118). These checks were inspected without new compiler execution or tests; no additional false-clean result was reproduced.

Evidence: [probe script](probes.py), [14 observed responses](probe-results.json), [isolated API log](api-probe.log).
