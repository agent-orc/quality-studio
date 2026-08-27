---
name: verify
description: Build and run Quality Studio's API to observe a real behavior change (not just dotnet test). Use for backend changes to src/QualityStudio.Api or src/AgentOrchestrator.CodeQuality.
---

# Verifying QualityStudio.Api at runtime

The API (`src/QualityStudio.Api`) is an ASP.NET Core minimal-API app. To
observe a change instead of just testing it, run the built app against a
throwaway content root and hit it with `curl`.

## Launch

```bash
mkdir -p /tmp/qs-verify/{host/.quality-studio,in-root-repo}
echo "namespace Sample; public sealed class Subject;" > /tmp/qs-verify/in-root-repo/Sample.cs

# Optional: seed a persisted registry at /tmp/qs-verify/host/.quality-studio/repositories.json
# (array of RepositoryRegistration objects — id, displayName, rootPath, globalInputsDirectory,
# inputBudgetCharacters, enabledReviewKinds) to test registry/boot behavior directly.

QualityStudio__RepositoryRoot=/tmp/qs-verify/in-root-repo \
QualityStudio__AllowedRoots__0=/tmp/qs-verify/in-root-repo \
QualityStudio__Security__Mode=Local \
ASPNETCORE_URLS=http://127.0.0.1:5177 \
ASPNETCORE_CONTENTROOT=/tmp/qs-verify/host \
dotnet run --project src/QualityStudio.Api/QualityStudio.Api.csproj --no-launch-profile \
  > /tmp/qs-verify/api.log 2>&1 &
```

`ASPNETCORE_CONTENTROOT` controls where the API looks for
`.quality-studio/repositories.json` (the persisted repository registry) —
this is the lever for reproducing registry/boot scenarios without touching
the real dev checkout. `QualityStudio:Security:Mode=Local` skips the
Hosted-mode client-credential dance so plain `curl` works.

Wait a few seconds, then tail `/tmp/qs-verify/api.log` — startup warnings/errors
(e.g. quarantined repositories, config problems) print there before
"Application started."

## Drive it

```bash
curl -s http://127.0.0.1:5177/health
curl -s http://127.0.0.1:5177/api/repos | python3 -m json.tool
curl -s "http://127.0.0.1:5177/api/repos/<repoId>/file?path=Sample.cs"
curl -s -X POST http://127.0.0.1:5177/api/repos -H "Content-Type: application/json" \
  -d '{"displayName":"X","rootPath":"/some/path","inputBudgetCharacters":12000,"enabledReviewKinds":["code"]}'
```

## Clean up

```bash
kill %1  # the backgrounded dotnet run job
rm -rf /tmp/qs-verify
```

## Gotchas

- The exception middleware (`Program.cs`) only ever returns the generic
  `PublicTitle` to HTTP clients, never the detailed exception `Message`
  (which may contain paths/allowed-roots) — check `/tmp/qs-verify/api.log`
  for the detailed, path-bearing message, not the HTTP response body.
- `RepositorySnapshotPrewarmer` resolves `RepositoryRegistry` as part of
  ASP.NET Core's hosted-service startup, so a `RepositoryRegistry`
  constructor crash previously failed the whole boot, not just one request.
- Frontend changes: `cd frontend && npx ng build --configuration development`
  compiles templates and catches binding errors; there is no headless
  browser harness in this repo, so DOM-level UI verification needs a manual
  `ng serve` + browser check.
