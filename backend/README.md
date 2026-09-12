# Backend

The backend is a .NET 10 solution with three production projects and two test projects.
The solution and shared build properties live at the repository root so all projects
use the same framework, dependency-lock and warning policy.

| Path | Responsibility |
| --- | --- |
| `src/AgentOrchestrator.CodeQuality/` | Reusable analysis engine and its embedded prompts/catalogues. No HTTP hosting dependency. |
| `src/QualityStudio.Api/` | HTTP endpoints, repository access and registration, background work, security and static UI hosting. |
| `src/quality-cli/` | Command-line host over the same analysis engine. |
| `tests/AgentOrchestrator.CodeQuality.Tests/` | Engine and CLI tests with their recorded fixtures. |
| `tests/QualityStudio.Api.Tests/` | API contract, security and hosting integration tests. |
| `tests/*.cs` | Shared isolated-directory and quality-data test helpers. |

Run these commands from the repository root:

```sh
dotnet build QualityStudio.slnx
dotnet test QualityStudio.slnx
dotnet run --project backend/src/QualityStudio.Api
dotnet run --project backend/src/quality-cli -- scan .
dotnet pack backend/src/AgentOrchestrator.CodeQuality --configuration Release
```

`npm start` launches the API and Angular together. Shared schemas, authored review
rules and sample contracts remain in `schemas/`, `rules/` and `samples/` at the root.
The root `tests/` directory contains repository-tooling tests and coverage thresholds;
backend fixtures belong here instead of mixing into that directory.

Production dependencies point inward: API and CLI reference the analysis engine;
the engine does not reference either host. Backend code must not depend on Angular
source files. See [the API contract](../docs/api.md) and
[the analysis-package README](src/AgentOrchestrator.CodeQuality/README.md).

## Existing local checkouts

Before starting the relocated API for the first time, stop the old API process.
If the checkout has local registry/cache data under
`src/QualityStudio.Api/.quality-studio` (relative to the repository root), preserve
a backup and copy that directory to `backend/src/QualityStudio.Api/.quality-studio`.
Do not overwrite a registry that already exists at the destination. These ignored
local files are not moved by Git. Keep custom externally configured storage paths
as configured. The development launcher uses the new project path automatically.
