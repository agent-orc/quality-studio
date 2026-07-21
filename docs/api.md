# Quality Studio API

The finding handover endpoints and Agent Studio configuration are documented in
[concepts/handover.md](concepts/handover.md).

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
budget, enabled review kinds, and archive state. This is the single canonical registry;
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

## Endpoint samples

The following commands were exercised against the live host at
`http://127.0.0.1:5127` on 2026-07-11:

```shell
curl "http://127.0.0.1:5127/api/tree?path="
# 200 {"path":".","nodes":[...]} (1 project root)

curl "http://127.0.0.1:5127/api/file?path=src/QualityStudio.Api/appsettings.json"
# 200 {"path":"src/QualityStudio.Api/appsettings.json","content":"...","metaDocuments":[]}

curl "http://127.0.0.1:5127/api/scan"
# 200 {"files":[...],"freshCount":0,"staleCount":0,"missingCount":20}

curl "http://127.0.0.1:5127/api/security/scan"
# 200 {"verdict":"pass","available":true,"scanner":"gitleaks",...}

curl "http://127.0.0.1:5127/api/inputs"
# 200 {"level":"file","kinds":{"code":{"inputs":[...],"omissions":[...]},...}}

curl -X POST "http://127.0.0.1:5127/api/review" -H "Content-Type: application/json" -d "{}"
# 501 {"title":"Review runner unavailable","status":501,"detail":"Review triggering requires the optional QS-6 review runner, which is not available in this build."}
```

All repository operations also have a scoped form, for example
`/api/repos/payments/tree?path=`, `/api/repos/payments/file?path=README.md`,
`/api/repos/payments/scan`, `/api/repos/payments/security/scan`,
`/api/repos/payments/inputs`, `/api/repos/payments/handover`, and
`/api/repos/payments/review`. The unscoped routes above remain aliases for the active
`default` registration so existing callers continue to work.

Paths are always repository-relative. Absolute paths and traversal outside the
selected repository root return RFC problem-details responses.
