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

The `GET /api/review/runs` and `GET /api/review/runs/{id}` response states,
including terminal `partial`, are documented in
[review-runs.md](review-runs.md#review-run-api-state).

Run the development host from the repository root:

```powershell
$env:QualityStudio__RepositoryRoot = (Get-Location).Path
dotnet run --project backend/QualityStudio.Api
```

`QualityStudio:RepositoryRoot` defaults to `../../..` relative to the API content root.
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

The browser UI sends the token in `Authorization` and, on every mutation, the matching `X-Client-Id`.
It learns the client id from `GET /api/v1/session` before its first mutation, so an operator enters
only the token.

### Signing in through Agent Studio

A hosted deployment can additionally accept browser sessions from one Agent Studio
(contract `agent-studio-integration/1`, Agent Studio Dossier AGT-3352, decision D4 option C). It is
configured by `AgentStudio:Federation:Issuer`; see [`deployment.md`](deployment.md#signing-in-through-agent-studio).
Without that key `POST /auth/agent-studio` answers `404`, and nothing below applies.

Agent Studio answers its `/api/integrations/quality-studio/open` route with a self-submitting form that
posts one field, `handoff`, to `POST /auth/agent-studio`. The value is a compact JWS signed with
Ed25519 (`alg: EdDSA`) whose claims are `iss`, `aud`, `sub`, `sid`, `role` (`viewer`, `operator`,
`owner`), `repos` (repository ids), `target`, `jti`, `iat`, `exp` and `ver: 1`. The receiver checks,
in this order, and answers the browser with a short page naming the reason:

| Check | Refusal |
| --- | --- |
| `Origin` is exactly the origin of `StudioPublicUrl` | `403` |
| a `handoff` field is present | `400` |
| the signature verifies against the pinned keys or the issuer's JWKS, `alg` is `EdDSA` | `401` |
| `iss` and `aud` match the configuration | `401` |
| `ver` is a supported version (`1`) | `422` |
| `iat`/`exp` span at most 60 seconds and `now` lies inside them, with 30 seconds of clock skew | `401` |
| `sub`, `sid`, `jti`, `role` and `repos` are well-formed | `401` |
| `target` is a relative path (one leading `/`, no `//`, no backslash) | `400` |
| no revocation covers the `sid` or `sub` since `iat` | `401` |
| the `jti` was never used before | `401` |

Used `jti` values are kept in `<data root>/federation/used-assertions.json` until the assertion
could no longer be valid, so a replayed body is refused after a restart too. On success the response
is `303 See Other` to `target`, with `Cache-Control: no-store` and two cookies: `__Host-qs-session`
(HttpOnly, Secure, SameSite=Lax, Path=/) holding a random session handle, and `__Host-qs-csrf`
holding the session's CSRF token for this origin's script. The request body is never logged; the log
names the `sub` and the role.

A session ends after 60 idle minutes, 12 hours after it was opened, when the host restarts, or when
Agent Studio revokes it. Its rights come from the assertion alone:

| Role | May |
| --- | --- |
| `viewer` | every read, and the side-effect-free `POST`s (`review/estimate`, `guidelines/impact`, `scope/rules/preview`, `rules/custom/validate`) |
| `operator` | also finding states, threads, starting, pausing, resuming, cancelling and pinning reviews, `change-review`, sensor scans, attack-coverage judgements and handover |
| `owner` | also the repository's rule pool, guidelines, scope rules and ignore list, and registering, changing or archiving a repository named in `repos` |

A repository outside `repos` is `404`, as for a bearer client. A session never holds the host-wide
standing of a registrar client: global rule changes and the Agent Studio import stay `403`. Every
`POST`, `PUT`, `PATCH` and `DELETE` of a session must carry `Origin` equal to this host's own origin
and `X-CSRF-Token` equal to the session's token (`403 Cross-origin request refused`,
`403 A valid X-CSRF-Token header is required`); it needs no `X-Client-Id`. Spending routes count
against the session's user, whatever headers the browser adds. A request that carries
`Authorization` is judged by its token alone, never by a cookie next to it.

A page load without a session (`/`, `/index.html` or a client route) is redirected to
`<StudioPublicUrl>/api/integrations/quality-studio/open?project=<repo>&return=<relative path>`, so
a bookmark signs in through Agent Studio and returns to the same place; `project` is the `repo`
query parameter until registrations carry an Agent Studio `externalRef`. Static assets are not gated.
An API `401` without `Authorization` carries `X-QS-Sign-In: agent-studio`, and the UI then reloads
into that redirect.

### Integration routes

| Route | Who | Answer |
| --- | --- | --- |
| `GET /api/v1/session` | any authenticated caller | `mode`, `kind` (`local`, `client`, `session`), `clientId` for a bearer client, `subject`, `role`, `repositories` and `csrfToken` for a session, and `federation` (`enabled`, `studioPublicUrl`) |
| `POST /api/v1/federation/revoke` | a bearer client listed in `AgentStudio:Federation:ServiceClients` (default `agent-studio`) | body `{"sid": "…"}` and/or `{"sub": "…"}`; ends every matching session at once and refuses hand-offs for them issued before the call. `{"revoked": <count>}` |
| `GET /api/v1/capabilities` | any authenticated caller | [`integration-capabilities.v1`](../schemas/integration-capabilities.v1.schema.json): product version, `contract: "agent-studio-integration/1"`, `contracts: ["agent-studio-integration/1"]`, `features` (`deep-link/1`, `external-ref`, `change-review-store`, and `federation` when configured), the hand-off versions and audience, and whether spending routes have a reviewer CLI (`spend.providerAvailable`) |

The routes Agent Studio calls also answer under `/api/v1`, with the same rights and limits:
`/api/v1/repos` (`GET`, `POST`), `/api/v1/repos/import-from-agent-studio`, `/api/v1/repos/{id}`
(`PUT`, `DELETE`), and below `/api/v1/repos/{id}`: `file`, `report`, `review/estimate`, `review`,
`review/runs`, `review/runs/{runId}`, `review/runs/{runId}/report`, `change-review`, `handover` and
`sensors/{sensor}/scan`. The unversioned paths remain.

## Deep link v1

Quality Studio owns its URLs; Agent Studio builds links from this table only. Every parameter is
routing data, none is a credential.

| Parameter | Values | Meaning |
| --- | --- | --- |
| `repo` | repository id, for example `proj-002` | Required. Without other parameters, opens the repository dashboard. |
| `view` | `dashboard`, `change`, `finding`, `explorer` | Target view. `dashboard` and any value this version does not implement open the repository dashboard; `change` does so until the change view exists (`change-review-store` absent from the capabilities). `finding` and `explorer` keep the position of the parameters below. |
| `base`, `head` | 40-character lowercase SHAs | For `view=change`. `base` is the merge-base, never a branch tip. |
| `finding`, `path`, `kind`, `location` | as before | Finding fingerprint, repository-relative path, review kind and location index, unchanged. |
| `card` | Agent Studio card key, for example `AGT-3352` | Display only: renders "Back to AGT-3352", a link to `StudioPublicUrl`. Never used for authorization, and ignored unless it looks like a card key and the host names a Studio. |

The UI drops `view` from the address once it has opened the target; `path`, `finding` and `card`
then describe the position.

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

### External references

A registration may carry `externalRefs`, stable references to the repository in another system.
`POST` and `PUT /api/repos` accept them; `PUT` without the field keeps the stored ones, `[]` clears
them. Every repository response returns them.

```shell
curl -X POST "http://127.0.0.1:5127/api/repos" \
  -H "Content-Type: application/json" \
  -d '{"id":"proj-002","displayName":"Agent Studio","rootPath":"/repos/agent-studio","inputBudgetCharacters":12000,"enabledReviewKinds":["code"],"externalRefs":[{"system":"agent-studio","id":"PROJ-002","url":"https://studio.example/projects/PROJ-002"}]}'

curl "http://127.0.0.1:5127/api/v1/repos?externalRef=agent-studio:PROJ-002"
# 200 {"repositories":[{"id":"proj-002", ..., "externalRefs":[{"system":"agent-studio","id":"PROJ-002","url":"..."}]}]}
```

- `system` is lowercase letters, digits, dots and hyphens; `id` is 1 to 128 non-whitespace
  characters; `url` is optional and must be absolute `http(s)`. At most 16 references.
- A `system:id` pair is unique across the registry, archived registrations included, and compares
  case-insensitively. A second registration with the same pair is refused with
  `409 Conflict`, title `External reference already registered`.
- `GET /api/v1/repos?externalRef=<system>:<id>` returns at most one active, servable repository in
  `repositories`, or `404` when none carries the reference. A malformed value returns `400`. Without
  `externalRef` the route lists the active repositories the caller may access.
- A registration whose `rootPath` does not exist yet is refused with `400` and the typed reason
  `"reason": "root-missing"` in the problem body. Nothing is stored, so the caller retries the same
  request, same `id`, once the directory exists.
- `GET /api/v1/capabilities` announces contract `agent-studio-integration/1` and the
  `external-ref`, `change-review-store`, and deep-link features, plus federation when enabled.

Everything else keeps path-as-identity: the Agent Studio import below still skips by normalized path.

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
| `coverage` | `reportPaths`, `profile`, `target`, `workingDirectory` |
| `dependencies` | `ecosystems` |
| `dotnet-build` | `target` |
| `gitleaks` | `mode`, `range`, `configPath`, `baselinePath` |

`command` is not in any of them. A registration carrying one is `400 Analyzer commands are host-owned`
for every identity, registrars included, unless the host sets
`QualityStudio:AnalyzerProfiles:AllowInlineCommands`. An unknown key is
`400 Unsupported sensor configuration key`, and a `profile` the host does not offer is
`400 Unknown analyzer profile`. The host-owned profiles are described in
[`deployment.md`](deployment.md#analyzer-profiles).

### Risk, complexity and directory projections

`GET /api/repos/{repoId}/risk?days=90` rows carry `complexity` for C#, TypeScript and JavaScript files
(`null` otherwise): `cyclomatic` and `cognitive` summed over the file's functions, `maxCyclomatic`,
`maxCognitive`, `functions`, the 0–100 `pressure` that enters `riskScore`, and the three most complex
functions as `hotspots`. `GET /api/repos/{repoId}/complexity?path=<file>` lists every function of one
file with its line span; any other file type is `400`. The metrics and the score are defined in
[`coverage-and-risk.md`](coverage-and-risk.md).

In the tree, a container's `kinds.<kind>.projection` is present when the container has no grade of its
own but some descendant files do:

```json
"code": { "direct": "missing", "score": null, "band": null,
  "projection": { "score": 79, "band": "C", "gradedFiles": 40, "files": 199, "weightedLines": 13337,
                  "basis": "size-weighted-file-grades" } }
```

It is the line-weighted mean of those files' grades — a projection, not a review. It disappears as soon
as an aggregate review grades the container ([`hierarchy-aggregation.md`](hierarchy-aggregation.md)).

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

curl "http://127.0.0.1:5127/api/file?path=backend/QualityStudio.Api/appsettings.json"
# 200 {"path":"backend/QualityStudio.Api/appsettings.json","content":"...","metaDocuments":[],"largeFile":null}

curl "http://127.0.0.1:5127/api/scan"
# 200 {"files":[...],"freshCount":0,"staleCount":0,"policyDriftCount":0,"missingCount":20,
#      "invalidCount":0}
# Each file carries a state of fresh, stale, policyDrift, missing, or invalid. `invalid` means the
# subject's sidecar exists but its JSON, schema version, or required fields could not be validated;
# it is never counted as fresh, stale, or reviewed coverage.

curl "http://127.0.0.1:5127/api/security/scan"
# 200 {"verdict":"pass","available":true,"scanner":"gitleaks",...}

curl "http://127.0.0.1:5127/api/security/attack-coverage?path=backend/QualityStudio.Api"
# 200 {"cellCount":...,"notYetCheckedCount":...,"staleCount":...,"rows":[...]}

curl -X POST "http://127.0.0.1:5127/api/security/attack-coverage/judgements?path=backend/QualityStudio.Api" \
  -H "Content-Type: application/json" \
  -d '{"assessmentId":"assessment-42","boundaryId":"...","attackId":"OWASP-API7-SSRF","verdict":"pass","reasoning":"The target is selected from a fixed allowlist.","evidence":[{"kind":"code","reference":"src/Api.cs#symbol:Fetch","summary":"Allowlist checked immediately before the HTTP call."}],"deterministicSensorInput":[],"source":"agent","reviewer":{"agent":"security-reviewer","model":"routed-model","thinkingLevel":"routed-level"},"tokenCost":{"inputTokens":1200,"outputTokens":180,"cachedInputTokens":0,"reasoningOutputTokens":80},"commit":"...","commitRange":"base..head"}'
# 201 and appends attacks/coverage-ledger.jsonl in the project's data root

curl "http://127.0.0.1:5127/api/sensors"
# 200 {"sensors":[{"id":"dependencies","version":"1.1.0","scopes":["repository","path"],"enabled":true,"available":true,...},...]}

curl -X POST "http://127.0.0.1:5127/api/sensors/dependencies/scan?path=frontend"
# 200 {"available":true,"unavailableReason":null,"findings":[...],"provenance":{...}}

curl -X POST "http://127.0.0.1:5127/api/sensors/boundaries/scan"
# 200 {"available":true,"findings":[...],"provenance":{"sensorId":"boundaries",...}}
# also writes boundaries/inventory.json in the project's data root

curl "http://127.0.0.1:5127/api/inputs"
# 200 {"level":"file","kinds":{"code":{"inputs":[...],"omissions":[...]},...}}

curl "http://127.0.0.1:5127/api/rules?kind=security&adapter=dotnet"
# 200 {"catalogueVersion":"1.6.0","filter":{...},"sources":["built-in"],"valid":true,"diagnostics":[],
#      "applicability":{"scope":"default","packs":["house-style"],...},"packs":[...],"scopes":{...},"rules":[...],"traces":[...]}

curl -X PUT "http://127.0.0.1:5127/api/repos/agent-studio-dev/rules/applicability?scope=project" \
  -H "Content-Type: application/json" -d '{"packs":["dotnet-service","angular-app"],"reason":"ASP.NET Core API with an Angular client."}'
# 200 the re-resolved pool; writes .quality/rules/applicability.json and appends the project audit trail

curl -X PUT "http://127.0.0.1:5127/api/repos/agent-studio-dev/rules/overrides/QS-CS-004?scope=project" \
  -H "Content-Type: application/json" -d '{"severity":"low","reason":"Snapshot tests share a fixture by design."}'
# 200 the re-resolved pool; 400 with "diagnostics" when the change would make the pool invalid

curl -X POST "http://127.0.0.1:5127/api/repos/agent-studio-dev/rules/custom/validate" \
  -H "Content-Type: application/json" -d '{"content":"---\nid: AS-CS-001\n..."}'
# 200 {"valid":false,"id":"AS-CS-001","rule":null,"diagnostics":[{"message":"requires a non-empty '## Detection' section."}]}

curl "http://127.0.0.1:5127/api/repos/agent-studio-dev/rules/export?scope=project" -o rule-set.json
# 200 rule-set.v1 attachment: applicability, overrides, customRules, packs, libraryVersion, digest

curl -X POST "http://127.0.0.1:5127/api/repos/other/rules/import?scope=project" -H "Content-Type: application/json" \
  -d "{\"ruleSet\":$(cat rule-set.json),\"mode\":\"merge\",\"dryRun\":true}"
# 200 {"valid":true,"applied":false,"modifiedSinceExport":false,"changes":[...],"diagnostics":[],"pool":{...}}

curl "http://127.0.0.1:5127/api/repos/agent-studio-dev/rules/audit?scope=global&limit=20"
# 200 {"scope":"global","entries":[{"at":"...","actor":"...","action":"override.set","target":"QS-CS-004","reason":"..."}]}

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

curl -X POST "http://127.0.0.1:5127/api/review/estimate" -H "Content-Type: application/json" \
  -d '{"path":".","kind":"code","optInSensors":["dotnet-build"],"refreshSensors":false}'
# 200 {...,"sensorPlan":{"expectedDurationMs":180000,"cacheNote":"Keyed to 9e79cb41abcd.",
#      "sensors":[{"sensorId":"dotnet-build","decision":"run","optIn":true,"optedIn":true,...},
#                 {"sensorId":"eslint","decision":"cached","expectedDurationMs":0,...}]}}
# Build sensors (dotnet-build, angular-compiler) run only when named in optInSensors; see review-runs.md.

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
not as unlimited quota. Its `auth` array holds one login state per provider
(`provider`, `state` = `ok` | `failed` | `unknown`, `checkedAt`, `detail`,
`source` = `review-run` | `quota-probe` | `none`), covering every quota provider
plus any provider a review has used; see
[`review-runs.md`](review-runs.md#provider-and-login-failures).

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

## Agent Studio integration (v1)

Routes under `/api/v1/` belong to the versioned contract `agent-studio-integration/1` between Quality
Studio and Agent Studio. They follow the same authentication as every other route: none in Local
mode, a bearer token or federated session in hosted mode, and the matching `X-Client-Id` or
`X-CSRF-Token` on mutations. Repository routes also answer under their unversioned paths; the
capabilities, session, revocation and verdict store routes belong to the v1 contract.

```shell
curl "http://127.0.0.1:5127/api/v1/capabilities"
# 200 {"product":"quality-studio","productVersion":"…","contract":"agent-studio-integration/1","contracts":["agent-studio-integration/1"],
#      "features":["deep-link/1","external-ref","change-review-store",…]}

curl -X PUT "http://127.0.0.1:5127/api/v1/repos/proj-002/change-reviews" \
  -H "Content-Type: application/json" \
  -d '{"document":{…change-review-verdict.v1…},"provenance":{"producer":"agent-studio-review-plane","cardKey":"AGT-3352","round":2,"attemptId":"…"}}'
# 201 {"outcome":"stored","record":{…}} · 200 unchanged/replaced · 409 a different verdict is stored
# 422 unknown schema or schemaVersion, a document outside the v1 schema, or another repository

curl "http://127.0.0.1:5127/api/v1/repos/proj-002/change-reviews?base=<sha>&head=<sha>&previous=<sha>"
# 200 {"commits":{"base":true,"head":true,"previous":true},"currentKey":{…},"records":[…],
#      "excerpts":{…},"roundOnly":{…},"permissions":{"runReview":true}}
```

The capabilities document names no repository, so any authenticated client can read it. A
consumer degrades a surface whose feature is missing instead of failing. The change-review store,
the cache-first `POST /api/repos/{id}/change-review`, and the response shapes are described in
[change-reviews.md](change-reviews.md#change-review-store).

### Deep link v1

The UI reads its position from query parameters. Every parameter is routing data. None is a
credential.

| Parameter | Values | Meaning |
| --- | --- | --- |
| `repo` | repository id, for example `proj-002` | The repository. Without other parameters, the repository dashboard opens. |
| `view` | `change` | Opens the change view for a commit range. Other values open the dashboard. |
| `base`, `head` | 40-character lowercase SHAs | Required for `view=change`. `base` is the merge-base of the reviewed range, never a branch tip. |
| `previous` | 40-character lowercase SHA | Optional with `view=change`: the previous round's head. Enables "This round only". |
| `card` | task key, for example `AGT-3352` | Display only: the change view's title. Never used for authorization. |
| `path`, `kind`, `finding`, `location`, `theme` | as before | Unchanged explorer, review and theme position. |

```
/?repo=proj-002&view=change&base=<merge-base sha>&head=<result sha>&card=AGT-3352&previous=<round-1 result sha>
```

Opening a change link never starts a review. A range without a stored verdict shows **Not
reviewed**. A range whose base or head commit is missing from the checkout shows **Commit not
present on this host**.
