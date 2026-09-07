# Quality Studio API

The finding handover endpoints and Agent Studio configuration are documented in
[concepts/handover.md](concepts/handover.md).
Review token persistence, usage response semantics, and quota ownership are
documented in [usage-telemetry.md](usage-telemetry.md).
Attack catalogue, ledger, provenance, and matrix semantics are documented in
[attack-coverage.md](attack-coverage.md).
Where the host files what a run generates — sidecars, ledgers, findings state and
run reports, none of it inside the analysed checkout — is documented in
[data-root.md](data-root.md).

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
The same repository is the only allowed root by default. Deployments that register
repositories below another neutral host root must supply it through configuration,
for example `QualityStudio__AllowedRoots__0=/srv/source` in the host environment;
machine-specific paths do not belong in `appsettings.json`.
On first start it seeds the repository with id `default`; existing single-repository
deployments therefore need no configuration change. CORS origins are configured with
the `QualityStudio:AllowedOrigins` array and default to `http://localhost:4200`.

Repository registrations are server-owned state persisted at
`<API content root>/.quality-studio/repositories.json`. Each entry stores its id,
display name, normalized root path, optional global inputs directory, input character
budget, enabled review kinds, sensor enablement/configuration, and archive state. This is the single canonical registry;
there are no environment-specific registry copies. Repository roots must be existing
directories with a `.git` directory or worktree `.git` file.

## Authentication

`QualityStudio:Security:Mode` decides who may call the API. Deployment recipes, the full environment
variable table and how Agent Studio gets a token are in [`deployment.md`](deployment.md).

**Local** (default) authenticates nobody: every request is treated as a registrar with wildcard
repository access, and `Authorization` and `X-Client-Id` are ignored. Because of that the host refuses
to start when it is bound anywhere but loopback, unless
`QualityStudio:Security:AllowNonLoopbackLocalMode` is set deliberately.

**Hosted** requires a bearer token per request and, on `POST`, `PUT`, `PATCH` and `DELETE`, an
`X-Client-Id` header matching the client that token belongs to:

```shell
curl "https://quality.example/api/repos" \
  -H "Authorization: Bearer $QUALITY_STUDIO_TOKEN"

curl -X POST "https://quality.example/api/repos/payments/review" \
  -H "Authorization: Bearer $QUALITY_STUDIO_TOKEN" \
  -H "X-Client-Id: agent-studio" \
  -H "Content-Type: application/json" \
  -d '{"path":"src/Payments.cs","kind":"code"}'
```

Clients are configured on the host, never in a repository. Each carries an id, the SHA-256 of its
token, the repository ids it may reach (or `*`), and whether it may register repositories. Mint one
with `node scripts/new-api-token.mjs <client-id>`; the token is printed once and only its hash reaches
the configuration.

| Failure | Response |
| --- | --- |
| No or unknown bearer token | `401 Authentication required`, `WWW-Authenticate: Bearer` |
| Mutation without a matching `X-Client-Id` | `401 A matching X-Client-Id is required for mutations` |
| Repository the client may not reach | `404 Repository not found` — existence is not disclosed |
| Registration or repository mutation without registrar privilege | `403 Repository registration is not permitted` |
| Plain HTTP while `RequireHttps` is on | `400 HTTPS is required` |
| More than `MaxConcurrentRequests` in flight, or `SpendRequestsPerMinute` exceeded on a spending route | `429` |
| Body larger than `MaxRequestBodyBytes` | `413 Request body is too large` |

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

### Large files

`GET /api/file` returns at most `QualityStudio:Limits:MaxFileBytes` (2 MB by default). A larger file is
answered with a capped prefix of `LargeFilePreviewBytes` (64 KiB by default), cut back to the last
complete UTF-8 sequence so the preview never ends inside a character, plus a `largeFile` envelope:

```jsonc
{
  "path": "frontend/dist/main.js",
  "content": "…",          // the prefix, not the file
  "sizeBytes": 5242880,     // always the true size on disk
  "largeFile": {
    "sizeBytes": 5242880,   // the file
    "limitBytes": 2097152,  // above this the response is a preview
    "returnedBytes": 65536  // how much of it "content" carries
  }
}
```

`largeFile` is `null` whenever the whole file was returned. The browser already renders files above
200 KB as a "large file" placeholder, so this is a server-side lid on the response, not on the UI.

### Git state

`GET /api/tree` carries a `gitState` object when the hierarchy could not follow the working tree —
git is missing, or `git status` failed in that repository:

```json
{ "path": ".", "nodes": [], "gitState": { "status": "unavailable", "detail": "git status failed in this repository, …" } }
```

The field is absent when git answered. The nodes are then the last state git could report; the API does
not fall back to walking the filesystem.

### Sensor configuration

`GET /api/sensors` reports each sensor's availability, cached per host for
`QualityStudio:Limits:SensorAvailabilityCacheSeconds` (5 minutes by default) because every probe starts
a real tool process.

A repository's sensor configuration is checked against a per-sensor allowlist on `POST /api/repos` and
`PUT /api/repos/{repoId}`:

| Sensor | Accepted keys |
| --- | --- |
| `sarif`, `roslyn`, `eslint` | `profile`, `reportPath`, `workingDirectory` |
| `tsc` | `profile`, `reportPath`, `workingDirectory`, `producerVersion` |
| `coverage` | `reportPaths` |
| `dependencies` | `ecosystems` |
| `dotnet-build` | `target` |
| `gitleaks` | `mode`, `range`, `configPath`, `baselinePath` |

`command` is not in any of them. A registration carrying one is `400 Analyzer commands are host-owned`
for every identity, registrars included, unless the host sets
`QualityStudio:AnalyzerProfiles:AllowInlineCommands`. An unknown key is
`400 Unsupported sensor configuration key`, and a `profile` the host does not offer is
`400 Unknown analyzer profile`. The host-owned profiles are described in
[`deployment.md`](deployment.md#analyzer-profiles).

### Unavailable repositories

`GET /api/repos` carries an `unavailable` array next to `repositories`, listing registrations this host
loaded but cannot serve — a directory that disappeared (`status: "unavailable"`) or a path outside the
allowed roots (`status: "quarantined"`) — each with its `reason` and `rootPath`. Their own routes answer
`503 Repository unavailable`; every other repository is unaffected. Only registrations the calling
client may reach are listed.

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
# 200 {"path":"src/QualityStudio.Api/appsettings.json","content":"...","metaDocuments":[],"largeFile":null}

curl "http://127.0.0.1:5127/api/scan"
# 200 {"files":[...],"freshCount":0,"staleCount":0,"policyDriftCount":0,"missingCount":20,
#      "invalidCount":0}
# Each file carries a state of fresh, stale, policyDrift, missing, or invalid. `invalid` means the
# subject's sidecar exists but its JSON, schema version, or required fields could not be validated;
# it is never counted as fresh, stale, or reviewed coverage.

curl "http://127.0.0.1:5127/api/security/scan"
# 200 {"verdict":"pass","available":true,"scanner":"gitleaks",...}

curl "http://127.0.0.1:5127/api/security/attack-coverage?path=src/QualityStudio.Api"
# 200 {"cellCount":...,"notYetCheckedCount":...,"staleCount":...,"rows":[...]}

curl -X POST "http://127.0.0.1:5127/api/security/attack-coverage/judgements?path=src/QualityStudio.Api" \
  -H "Content-Type: application/json" \
  -d '{"assessmentId":"assessment-42","boundaryId":"...","attackId":"OWASP-API7-SSRF","verdict":"pass","reasoning":"The target is selected from a fixed allowlist.","evidence":[{"kind":"code","reference":"src/Api.cs#symbol:Fetch","summary":"Allowlist checked immediately before the HTTP call."}],"deterministicSensorInput":[],"source":"agent","reviewer":{"agent":"security-reviewer","model":"routed-model","thinkingLevel":"routed-level"},"tokenCost":{"inputTokens":1200,"outputTokens":180,"cachedInputTokens":0,"reasoningOutputTokens":80},"commit":"...","commitRange":"base..head"}'
# 201 and appends attacks/coverage-ledger.jsonl in the project's data root

curl "http://127.0.0.1:5127/api/sensors"
# 200 {"sensors":[{"id":"dependencies","version":"1.0.0","scopes":["repository","path"],"enabled":true,"available":true,...},...]}

curl -X POST "http://127.0.0.1:5127/api/sensors/dependencies/scan?path=frontend"
# 200 {"available":true,"unavailableReason":null,"findings":[...],"provenance":{...}}

curl -X POST "http://127.0.0.1:5127/api/sensors/boundaries/scan"
# 200 {"available":true,"findings":[...],"provenance":{"sensorId":"boundaries",...}}
# also writes boundaries/inventory.json in the project's data root

curl "http://127.0.0.1:5127/api/inputs"
# 200 {"level":"file","kinds":{"code":{"inputs":[...],"omissions":[...]},...}}

curl "http://127.0.0.1:5127/api/rules?kind=security&adapter=dotnet"
# 200 {"catalogueVersion":"1.2.0","filter":{...},"sources":["built-in"],"rules":[...],"traces":[...]}

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

curl "http://127.0.0.1:5127/api/models"
# 200 {"policyVersion":"...","models":[{"modelId":"gpt-...","capabilityTier":"frontier","routingStatus":"selectable",...}]}

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

`/api/quotas` is a global, presentation-safe snapshot from Runner's
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
