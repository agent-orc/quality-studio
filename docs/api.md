# Quality Studio API

The finding handover endpoints and Agent Studio configuration are documented in
[concepts/handover.md](concepts/handover.md).
Review token persistence, usage response semantics, and quota ownership are
documented in [usage-telemetry.md](usage-telemetry.md).
Attack catalogue, ledger, provenance, and matrix semantics are documented in
[attack-coverage.md](attack-coverage.md).

Quality scorecards, Git-backed trends, registry comparison, and export formats
are documented in [quality-reports.md](quality-reports.md). Use
`GET /api/report` for all accessible registry repositories or
`GET /api/repos/{repoId}/report` for one repository. JSON is the default;
`?format=markdown|html|json|sarif` selects another representation.

Run the development host from the repository root:

```powershell
$env:QualityStudio__RepositoryRoot = (Get-Location).Path
dotnet run --project src/QualityStudio.Api
```

`QualityStudio:RepositoryRoot` defaults to `../..` relative to the API content root.
On first start it seeds the repository with id `default`; existing single-repository
deployments therefore need no configuration change. CORS origins are configured with
the `QualityStudio:AllowedOrigins` array and default to `http://localhost:4200`.

Repository registrations are server-owned state persisted at
`<API content root>/.quality-studio/repositories.json`. Each entry stores its id,
display name, normalized root path, optional global inputs directory, input character
budget, enabled review kinds, sensor enablement/configuration, and archive state. This is the single canonical registry;
there are no environment-specific registry copies. Repository roots must be existing
directories with a `.git` directory or worktree `.git` file.

## Repository registry

```shell
curl "http://127.0.0.1:5127/api/repos"

curl -X POST "http://127.0.0.1:5127/api/repos" \
  -H "Content-Type: application/json" \
  -d '{"id":"payments","displayName":"Payments","rootPath":"C:\\Projects\\payments","globalInputsDirectory":null,"inputBudgetCharacters":12000,"enabledReviewKinds":["code","security","performance"]}'

curl -X PUT "http://127.0.0.1:5127/api/repos/payments" \
  -H "Content-Type: application/json" \
  -d '{"displayName":"Payments API","rootPath":"C:\\Projects\\payments","globalInputsDirectory":null,"inputBudgetCharacters":16000,"enabledReviewKinds":["code","security"]}'

curl -X DELETE "http://127.0.0.1:5127/api/repos/payments"
```

`DELETE` archives a registration and never changes repository files. The last active
registration and the legacy-compatible `default` registration cannot be archived.

### One-click import from Agent Studio

```shell
curl -X POST "http://127.0.0.1:5127/api/repos/import-from-agent-studio"
# 200 {"results":[{"projectId":"PROJ-002","displayName":"Agent Studio","repositoryPath":"C:\\Projects\\...","status":"imported","repositoryId":"agent-studio","reason":null}, ...],"imported":1,"skipped":2,"failed":1}
```

Fetches Agent Studio's project list (`GET {AgentStudio:BaseUrl}/api/projects`, see
[concepts/handover.md](concepts/handover.md#project-discovery-contract)) and onboards
every non-archived project as a repository registration: `displayName` becomes the
registration's display name, `shortCode` seeds its id. The full project list is read
before any registry write, so a failure leaves the registry untouched: an offline or
unreachable Agent Studio returns `502 Bad Gateway`, and a missing/incomplete
`AgentStudio:BaseUrl` configuration returns `503 Service Unavailable`, both as
problem-details responses.

Each project appears exactly once in `results` with one of three statuses:

- `imported` — a new registration was created; `repositoryId` is set.
- `skipped` — a registration already exists for that normalized path (re-running the
  import is idempotent).
- `failed` — the project has no `repositoryPath`, the path does not resolve, or the
  directory does not exist on this host; `reason` explains why.

Archived Agent Studio projects are not considered candidates and never appear in
`results`.

## Endpoint samples

The following commands were exercised against the live host at
`http://127.0.0.1:5127` on 2026-07-11:

```shell
curl "http://127.0.0.1:5127/api/tree?path="
# 200 ETag: "..." {"path":".","nodes":[...]}

curl "http://127.0.0.1:5127/api/tree?path=" -H 'If-None-Match: "..."'
# 304 when the repository HEAD, index, and worktree are unchanged

curl "http://127.0.0.1:5127/api/project"
# 200 {"grades":[...],"findings":{...},"staleness":{...},"reviewCoverage":{...},
#      "testCoverage":{...},"metrics":{"languages":[...],"dependencyEdges":[...]},
#      "hotspots":[...]}

curl "http://127.0.0.1:5127/api/file?path=src/QualityStudio.Api/appsettings.json"
# 200 {"path":"src/QualityStudio.Api/appsettings.json","content":"...","metaDocuments":[]}

curl "http://127.0.0.1:5127/api/scan"
# 200 {"files":[...],"freshCount":0,"staleCount":0,"policyDriftCount":0,"missingCount":20}

curl "http://127.0.0.1:5127/api/security/scan"
# 200 {"verdict":"pass","available":true,"scanner":"gitleaks",...}

curl "http://127.0.0.1:5127/api/security/attack-coverage?path=src/QualityStudio.Api"
# 200 {"cellCount":...,"notYetCheckedCount":...,"staleCount":...,"rows":[...]}

curl -X POST "http://127.0.0.1:5127/api/security/attack-coverage/judgements?path=src/QualityStudio.Api" \
  -H "Content-Type: application/json" \
  -d '{"assessmentId":"assessment-42","boundaryId":"...","attackId":"OWASP-API7-SSRF","verdict":"pass","reasoning":"The target is selected from a fixed allowlist.","evidence":[{"kind":"code","reference":"src/Api.cs#symbol:Fetch","summary":"Allowlist checked immediately before the HTTP call."}],"deterministicSensorInput":[],"source":"agent","reviewer":{"agent":"security-reviewer","model":"routed-model","thinkingLevel":"routed-level"},"tokenCost":{"inputTokens":1200,"outputTokens":180,"cachedInputTokens":0,"reasoningOutputTokens":80},"commit":"...","commitRange":"base..head"}'
# 201 and appends .quality/attacks/coverage-ledger.jsonl

curl "http://127.0.0.1:5127/api/sensors"
# 200 {"sensors":[{"id":"dependencies","version":"1.0.0","scopes":["repository","path"],"enabled":true,"available":true,...},...]}

curl -X POST "http://127.0.0.1:5127/api/sensors/dependencies/scan?path=frontend"
# 200 {"available":true,"unavailableReason":null,"findings":[...],"provenance":{...}}

curl -X POST "http://127.0.0.1:5127/api/sensors/boundaries/scan"
# 200 {"available":true,"findings":[...],"provenance":{"sensorId":"boundaries",...}}
# also writes <repository>/.quality/boundaries/inventory.json

curl "http://127.0.0.1:5127/api/inputs"
# 200 {"level":"file","kinds":{"code":{"inputs":[...],"omissions":[...]},...}}

curl "http://127.0.0.1:5127/api/guidelines"
# 200 {"guidelines":[...],"catalogue":[...],"traces":[...]}

curl -X POST "http://127.0.0.1:5127/api/guidelines" -H "Content-Type: application/json" \
  -d '{"id":"api-boundaries","enabled":true,"priority":80,"kinds":["code"],"levels":["file"],"content":"Validate public boundary input."}'
# 201 and writes .quality/inputs/api-boundaries.md

curl -X POST "http://127.0.0.1:5127/api/guidelines/catalog/security-boundaries/install"
# 201 and installs an editable repository copy

curl -X POST "http://127.0.0.1:5127/api/guidelines/impact" -H "Content-Type: application/json" \
  -d '{"guideline":{"id":"api-boundaries","enabled":true,"priority":80,"kinds":["code"],"levels":["file"],"content":"Validate all public boundary input."},"samplePaths":["src/Api.cs"],"kind":"code"}'
# 200 {"addedCount":1,"removedCount":0,"changed":true,"files":[...]}

curl -X POST "http://127.0.0.1:5127/api/review" -H "Content-Type: application/json" -d "{}"
# 202 {"id":"review-...","state":"queued",...}

curl "http://127.0.0.1:5127/api/usage?since=2026-07-01T00:00:00Z&kind=code"
# 200 {"runs":12,"inputTokens":...,"byModel":[...],"byKind":[...],"byDay":[...],"byReviewRun":[...],"recent":[...]}

curl "http://127.0.0.1:5127/api/quotas"
# 200 {"at":"...","ttlSeconds":600,"providers":[...]}

curl -X POST "http://127.0.0.1:5127/api/findings/state" \
  -H "Content-Type: application/json" \
  -d '{"path":"src/Thing.cs","kind":"code","fingerprint":"sha256:...","state":"waived","author":"Ada","reason":"Covered by the migration policy.","expiresAt":"2026-09-01T00:00:00Z","expectedTimestamp":"2026-07-22T09:00:00Z"}'
# 200 {"fingerprint":"sha256:...","state":"waived",...}
```

Finding state mutations accept `open`, `accepted`, `waived`, or `false-positive`.
Author and reason are required; expiry is optional. `expectedTimestamp` is the state
timestamp returned by the file response and enables optimistic concurrency. A stale
write returns `409 Conflict` instead of replacing another reviewer's decision. See
[finding-lifecycle.md](finding-lifecycle.md) for identity, merge, and grading rules.

`since` is an optional ISO 8601 timestamp and `kind` is an optional exact review
kind (`code`, `security`, or `performance`). The usage response includes totals,
`byModel`, `byKind`, `byDay`, and `byReviewRun` aggregates plus the 50 newest
matching ledger entries. A `byReviewRun` item totals every operation sharing a
durable v2 `reviewRunId`; legacy v1 entries fall back to their per-operation
`runId`. Token totals treat unavailable token fields as zero while each recent
entry preserves `null`, distinguishing unreported usage from a reported zero.

`/api/quotas` is a global, presentation-safe snapshot from Coding Agent Runner's
quota service. It may return an empty `providers` array while credentials or
provider data are unavailable; callers must treat that as an unavailable state,
not as unlimited quota.

All repository operations also have a scoped form, for example
`/api/repos/payments/tree?path=`, `/api/repos/payments/file?path=README.md`,
`/api/repos/payments/project`, `/api/repos/payments/scan`, `/api/repos/payments/security/scan`,
`/api/repos/payments/security/attack-coverage`,
`/api/repos/payments/inputs`, `/api/repos/payments/handover`, and
`/api/repos/payments/review`. Finding state uses
`/api/repos/payments/findings/state`. Guideline CRUD, catalogue install, trace,
and impact also have scoped forms under `/api/repos/payments/guidelines`. Usage
has the scoped form
`/api/repos/payments/usage`; quotas are per signed-in user/provider and remain global.
The unscoped routes above remain aliases for the active
`default` registration so existing callers continue to work.

Paths are always repository-relative. Absolute paths and traversal outside the
selected repository root return RFC problem-details responses.
