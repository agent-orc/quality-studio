# Running Quality Studio

Two ways to run the product, and one integration to configure.

- **Local**, on the machine that owns the repositories: no tokens, loopback only.
- **Hosted**, one container serving API and UI on one port: every request needs a token.

The container image is built from the [`Dockerfile`](../Dockerfile) at the repository root. It builds
the browser bundle with Node 22, publishes the API with the .NET 10 SDK, and assembles an
`mcr.microsoft.com/dotnet/aspnet:10.0` runtime that runs as a non-root user and carries `git` (the API
starts git processes) and the pinned `gitleaks` binary with `QUALITY_GITLEAKS_PATH` set, so no scan
downloads anything at run time. `HEALTHCHECK` polls `/health`.

## Local

```powershell
npm start
```

The launcher binds the API to `http://127.0.0.1:5127` and serves the Angular dev server on `4200`
through [`frontend/proxy.conf.json`](../frontend/proxy.conf.json). Local mode treats every request as a
registrar with wildcard repository access and authenticates nobody, so the host refuses to start when
it is bound anywhere but loopback:

```
Quality Studio runs in Local mode, where every request is treated as a registrar, but it is
bound to http://0.0.0.0:5127, which is reachable from outside this machine. …
```

Fix it by binding loopback, by switching to hosted mode, or — if you genuinely want an unauthenticated
API on the network and understand that anyone who can reach it can register repositories and spend
model budget — by setting `QualityStudio__Security__AllowNonLoopbackLocalMode=true`.

Local API requests also validate their HTTP authority and browser origin. By default the
request Host must be loopback, even if another hostname resolves to the loopback listener.
A browser Origin must match the request origin or an exact entry in
`QualityStudio__AllowedOrigins`; opaque `null` origins and malformed origins are refused.
Cross-site Fetch Metadata without an Origin is refused. Headerless local CLI clients remain
supported. These checks prevent browser-origin abuse; they do not authenticate local processes.
The deliberate non-loopback override relaxes only the Host restriction, not the Origin check.
The development launcher adds its exact frontend origin, including a custom frontend port. Explicit
host Origin settings take precedence. Direct API startup defaults to localhost and 127.0.0.1 on port 4200.

## Hosted, in a container

```shell
docker build -t quality-studio:local .

node scripts/new-api-token.mjs agent-studio --registrar
# Token (shown once, store it in the calling client):
#   pP2s…                                   <- goes to Agent Studio
# Host configuration:
#   QualityStudio__Security__Clients__0__Id=agent-studio
#   QualityStudio__Security__Clients__0__CredentialSha256=9f2c…   <- goes to the host

docker run --rm \
  --publish 127.0.0.1:8080:8080 \
  --volume /srv/repositories:/repositories \
  --volume quality-studio-registry:/app/.quality-studio \
  --env QualityStudio__Security__Clients__0__Id=agent-studio \
  --env QualityStudio__Security__Clients__0__CredentialSha256=9f2c… \
  --env QualityStudio__Security__Clients__0__Repositories__0='*' \
  --env QualityStudio__Security__Clients__0__CanRegisterRepositories=true \
  quality-studio:local
```

[`docker-compose.yml`](../docker-compose.yml) is the same thing with the volumes named. The UI is on
`http://127.0.0.1:8080/`, the API under `/api` on the same port.

Hosted mode requires at least one client. A container started without one exits with
`Hosted mode requires at least one configured API client.` — that is deliberate: an image that fell
back to "no authentication" would publish an unauthenticated registrar API on `0.0.0.0`.

### Volumes

| Path | What lives there |
| --- | --- |
| `/repositories` | The working copies under review. The studio reads them and writes nothing back; the registry may only point inside `QualityStudio__AllowedRoots`. |
| `/app/.quality-studio` | The server-owned repository registry (`repositories.json`). Mount it, or every container replacement forgets which repositories were registered. |
| `/data` | Everything runs generate, one folder per analysed project: findings, grades, run reports, token ledgers, review sidecars, run journals. Mount it, or a container replacement loses every review the host has paid for. See [`data-root.md`](data-root.md). |

### Behind a reverse proxy

The image sets `QualityStudio__Security__RequireHttps=false` because the container itself speaks plain
HTTP. For TLS termination at a reverse proxy, enable HTTPS enforcement and configure the proxy's
actual connection IP as seen by the API:

```text
QualityStudio__Security__RequireHttps=true
QualityStudio__Security__TrustedProxies__0=10.20.30.40
```

Replace the example address with the deployed proxy address. The allowlist is empty by default;
without it, forwarded headers have no effect. Only explicit IP addresses are accepted, and proxy
forwarding requires Hosted mode. Both `X-Forwarded-For` and `X-Forwarded-Proto` must be supplied by the
proxy with matching value counts. Only one trusted hop is processed, before the HTTPS/authentication
checks. `X-Forwarded-Host` is ignored. A direct client or unknown proxy cannot make plain HTTP pass
the HTTPS requirement by supplying `X-Forwarded-Proto: https`.

Configure the edge to replace untrusted incoming forwarding headers, restrict accepted hostnames,
and keep the backend port private. The host refuses to start if automatic forwarding is enabled through
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`; use the explicit allowlist above. For multiple proxy hops,
terminate the chain at the configured trusted edge instead of trusting arbitrary intermediaries.
See [Microsoft's proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## Environment variables

Every `QualityStudio:*` configuration key maps to an environment variable by replacing `:` with `__`.

| Variable | Default | What it does |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` (image) | Listen addresses. In Local mode these must be loopback. |
| `QualityStudio__DataRoot` | per-user app data (`/data` in the image) | Where runs file what they generate, one folder per project. Never inside a reviewed working copy. |
| `QUALITY_STUDIO_DATA_ROOT` | — | The same override, and the one that wins. The `quality` CLI reads both, so a container that sets only `QualityStudio__DataRoot` still has the CLI and the host agree on where a project's data lives. |
| `QualityStudio__Security__Mode` | `Local` | `Local` (every request is a registrar) or `Hosted` (bearer token required). |
| `QualityStudio__Security__AllowNonLoopbackLocalMode` | `false` | Deliberately publish an unauthenticated Local-mode host beyond loopback. |
| `QualityStudio__Security__RequireHttps` | `true` | Refuse plain-HTTP `/api` requests and send HSTS. Hosted mode only. |
| `QualityStudio__Security__TrustedProxies__N` | empty | Explicit proxy IPs allowed to forward scheme and client address in Hosted mode. One symmetric hop; forwarded Host is ignored. |
| `QualityStudio__Security__MaxRequestBodyBytes` | `65536` | Largest accepted request body. |
| `QualityStudio__Security__MaxConcurrentRequests` | `32` | Concurrent request limit; excess is `429`. |
| `QualityStudio__Security__SpendRequestsPerMinute` | `5` | Per-client budget for routes that spend model tokens. |
| `QualityStudio__Security__Clients__N__Id` | — | Client id. Must match the `X-Client-Id` header on every mutation. |
| `QualityStudio__Security__Clients__N__CredentialSha256` | — | SHA-256 of the bearer token. The token itself never reaches the host configuration. |
| `QualityStudio__Security__Clients__N__Repositories__M` | — | Repository ids this client may reach, or `*`. |
| `QualityStudio__Security__Clients__N__CanRegisterRepositories` | `false` | Allow `POST /api/repos`, the Agent Studio import, and `PUT`/`DELETE /api/repos/{id}`. Requires `*`. |
| `QualityStudio__RepositoryRoot` | `../../..` (`/repositories` in the image) | Seeds the `default` registration on first start. |
| `QualityStudio__AllowedRoots__N` | the repository root | Directories registrations may point inside. Everything else is refused. |
| `QualityStudio__AllowedOrigins__N` | localhost / 127.0.0.1 on port 4200 | Exact CORS and Local-mode browser origins. The dev launcher sets its frontend origin. |
| `QualityStudio__Ui__RootPath` | `wwwroot` next to the API | The built browser bundle. Absent means an API-only host. |
| `QualityStudio__AnalyzerProfiles__Path` | `analyzer-profiles.json` next to the API | Host-owned analyzer profiles; replaces the embedded defaults when present. |
| `QualityStudio__AnalyzerProfiles__AllowInlineCommands` | `false` | Re-enables free-form `command` entries in repository sensor configuration. |
| `QualityStudio__Limits__MaxFileBytes` | `2097152` | Above this, `GET /api/file` returns a capped preview instead of the whole file. |
| `QualityStudio__Limits__LargeFilePreviewBytes` | `65536` | How much of an oversized file the preview carries. |
| `QualityStudio__Limits__SensorAvailabilityCacheSeconds` | `300` | How long a sensor availability probe is reused. `0` probes every request. |
| `QualityStudio__Limits__ProjectDashboardCacheEntries` | `32` | Bound on the project dashboard projection cache. |
| `QualityStudio__DefaultReviewTokenCap` | `100000` | Default token cap for a review run. |
| `QualityStudio__InputBudgetCharacters` | `12000` | Character budget for resolved review inputs. |
| `QUALITY_GITLEAKS_PATH` | unset (`/usr/local/bin/gitleaks` in the image) | An existing pinned Gitleaks binary. Set it and nothing is downloaded. |
| `QUALITY_GLOBAL_INPUTS` | unset | Fallback global inputs directory when a registration names none. |
| `AgentStudio__BaseUrl` / `__ClientId` / `__Project` / `__DryRun` | `null` / `null` / `null` / `true` | The Agent Studio host finding handover posts to. |
| `ReviewJobs__MaxConcurrency` | `2` | Concurrent review runs. |

## Analyzer profiles

Repository sensor configuration selects a **profile id**; it can never carry the command the host
executes. The embedded defaults:

| Sensor | Profile | Runs in | What it does |
| --- | --- | --- | --- |
| `eslint` | `eslint-frontend-sarif` | `frontend/` | The workspace's own ESLint, its nearest `eslint.config.*` and the SARIF formatter, resolved from the nearest `node_modules` up to the repository root. |
| `eslint` | `eslint-root-sarif` | `.` | The same for a workspace at the repository root. |
| `roslyn` | `roslyn-build-sarif` | `.` | `dotnet build --no-incremental` with a generated MSBuild import that gives every project its own SARIF 2.1 log in `.quality/preflight/roslyn/`. |
| `tsc` | `tsc-noemit` | the scanned directory | `node <typescript>/bin/tsc -p <tsconfig> --noEmit`; a solution-style `tsconfig.json` is checked through each project it references. |
| `tsc` | `tsc-frontend` | `frontend/` | The same for a workspace in `frontend/`. |

Why the Roslyn profile does not pass `-p:ErrorLog=…,version=2.1`: MSBuild splits a command-line property
at the comma, so the compiler wrote a SARIF **1.0** log the importer rejects, and one global file name
meant every project of a solution overwrote the previous one. An incremental build that skips the
compiler writes no log at all, which used to read as clean. The generated
`quality-studio-errorlog.targets` sets `ErrorLog` per project (`<project>-<path hash>-<tfm>.sarif%2Cversion=2.1`)
and is handed to MSBuild through `CustomAfterMicrosoftCommonTargets`; a repository that sets that
property itself has it overridden for the scan. A build that fails without reporting a compiler error is
unavailable, not partially clean.

The SARIF import honours `suppressions`: a result whose suppressions are all accepted (Roslyn's
`#pragma warning disable` and `[SuppressMessage]`, ESLint's disable comments) is not a finding, and the
scan reports how many it skipped as `suppressedFindings`. A suppression `underReview` or `rejected` keeps
the finding.

Availability probes run in the analysed repository and in the profile's working directory, so they
report the SDK its `global.json` selects and the Node tools its workspace installed; a missing
`npm ci` shows up as an unavailable sensor with the reason.

To ship your own, mount a file and name it in `QualityStudio__AnalyzerProfiles__Path`:

```json
{
  "profiles": [
    {
      "id": "house-lint",
      "sensor": "eslint",
      "command": "node tools/lint.js {target} --sarif {reportPath}",
      "workingDirectory": ".",
      "reportPath": ".quality/preflight/eslint.sarif",
      "description": "The house ESLint wrapper."
    }
  ]
}
```

`{repositoryRoot}`, `{target}`, `{reportPath}` and `{reportDirectory}` are expanded at run time; every path
stays confined to the repository. A profile can also ask for the analysed repository's tools:
`{nodeModule:<package path>}` (the nearest `node_modules/<package path>` from the working directory up to the
repository root), `{eslintConfig}` (the nearest ESLint flat config), `{tsconfig}` (the tsc sensor's project
file, one invocation per referenced project) and `{roslynErrorLogTargets}` (the per-project ErrorLog import).
A placeholder that cannot be resolved makes the scan unavailable with the reason instead of running a
command bound to fail. A `reportPath` ending in `/` names a directory whose `*.sarif` files are merged.
A `coverage` profile also receives `{outputDirectory}` — a fresh directory below the project's data root —
and its `reportPath` is a glob of the reports it writes there. `timeoutSeconds` (1–3600) time-boxes a
profile; the process tree is killed when it expires. A host file **replaces** the embedded defaults rather than extending them, so a
deployment that names its own profiles cannot silently fall back to a shipped command. A registration
that names a profile the host does not offer is refused with `400 Unknown analyzer profile`, and a
registration carrying a `command` with `400 Analyzer commands are host-owned` — for registrars too,
unless `QualityStudio__AnalyzerProfiles__AllowInlineCommands` is set.

## Connecting Agent Studio

Agent Studio needs one token. Mint it with `--registrar` if it should import repositories, without it if
it should only read one:

```shell
node scripts/new-api-token.mjs agent-studio --registrar
node scripts/new-api-token.mjs agent-studio --repositories payments
```

Every call carries the bearer token; every mutation additionally carries `X-Client-Id` matching the
client id, or the request is `401`.

```
Authorization: Bearer <token>
X-Client-Id: agent-studio
```

The routes the integration uses:

| Purpose | Route |
| --- | --- |
| List reachable repositories | `GET /api/repos` |
| Import a repository from Agent Studio | `POST /api/repos/import-from-agent-studio` (registrar) |
| Register a repository directly | `POST /api/repos` (registrar) |
| Estimate a review before spending | `POST /api/repos/{repoId}/review/estimate` |
| Start a review | `POST /api/repos/{repoId}/review` (spend-limited) |
| Poll one run | `GET /api/repos/{repoId}/review/runs/{id}` |
| List recent runs | `GET /api/repos/{repoId}/review/runs` |
| Read the terminal run report | `GET /api/repos/{repoId}/review/runs/{id}/report` |
| Read findings for a file | `GET /api/repos/{repoId}/file?path=…` |
| Read the repository scorecard | `GET /api/repos/{repoId}/report` |
| Hand findings back as tasks | `POST /api/repos/{repoId}/handover` (spend-limited) |
| Inspect the handover target | `GET /api/repos/{repoId}/handover` |

Nothing in Agent Studio changes for this; the table records what the integration needs to call. The
contracts are in [`api.md`](api.md) and [`concepts/handover.md`](concepts/handover.md).

Locally, none of this applies: Local mode authenticates nobody, `Authorization` and `X-Client-Id` are
ignored, and every request reaches every registered repository. That is the whole reason the host
refuses to leave loopback in that mode.

## When a registration cannot be served

A persisted registration whose directory disappeared, or that points outside `AllowedRoots`, does not
stop the host. It is loaded as unavailable and reported by `GET /api/repos`:

```json
{
  "repositories": [ … ],
  "unavailable": [
    {
      "id": "payments",
      "displayName": "Payments",
      "rootPath": "/repositories/payments",
      "status": "unavailable",
      "reason": "The repository directory does not exist."
    }
  ]
}
```

Its own routes answer `503`; every other repository keeps working. Repointing it with
`PUT /api/repos/{id}` lifts the quarantine without a restart.
