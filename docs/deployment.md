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
HTTP and the proxy terminates TLS. When the proxy forwards the original scheme, set
`QualityStudio__Security__RequireHttps=true` so a request that arrives over plain HTTP is refused; the
API also sends HSTS in that case. Publish the container port to loopback only and let the proxy own the
public address.

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
| `QualityStudio__Security__MaxRequestBodyBytes` | `65536` | Largest accepted request body. |
| `QualityStudio__Security__MaxConcurrentRequests` | `32` | Concurrent request limit; excess is `429`. |
| `QualityStudio__Security__SpendRequestsPerMinute` | `5` | Per-client budget for routes that spend model tokens. |
| `QualityStudio__Security__Clients__N__Id` | — | Client id. Must match the `X-Client-Id` header on every mutation. |
| `QualityStudio__Security__Clients__N__CredentialSha256` | — | SHA-256 of the bearer token. The token itself never reaches the host configuration. |
| `QualityStudio__Security__Clients__N__Repositories__M` | — | Repository ids this client may reach, or `*`. |
| `QualityStudio__Security__Clients__N__CanRegisterRepositories` | `false` | Allow `POST /api/repos`, the Agent Studio import, and `PUT`/`DELETE /api/repos/{id}`. Requires `*`. |
| `QualityStudio__RepositoryRoot` | `../../..` (`/repositories` in the image) | Seeds the `default` registration on first start. |
| `QualityStudio__AllowedRoots__N` | the repository root | Directories registrations may point inside. Everything else is refused. |
| `QualityStudio__AllowedOrigins__N` | `http://localhost:4200` | CORS origins for the development frontend. |
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
executes. The embedded defaults cover `eslint` (`eslint-frontend-sarif`, `eslint-root-sarif`), `roslyn`
(`roslyn-build-sarif`) and `tsc` (`tsc-noemit`). To ship your own, mount a file and name it in
`QualityStudio__AnalyzerProfiles__Path`:

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

`{repositoryRoot}`, `{target}` and `{reportPath}` are expanded at run time; every path stays confined to
the repository. A host file **replaces** the embedded defaults rather than extending them, so a
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
