# Quality Studio API

The finding handover endpoints and Agent Studio configuration are documented in
[concepts/handover.md](concepts/handover.md).

Run the development host from the repository root:

```powershell
$env:QualityStudio__RepositoryRoot = (Get-Location).Path
dotnet run --project src/QualityStudio.Api
```

`QualityStudio:RepositoryRoot` defaults to `../..` relative to the API content root.
Override any setting through normal ASP.NET Core configuration; the environment
variable above is the portable repository-root override. CORS origins are configured
with the `QualityStudio:AllowedOrigins` array and default to `http://localhost:4200`.

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

Paths are always repository-relative. Absolute paths and traversal outside the
configured root return RFC problem-details responses.
