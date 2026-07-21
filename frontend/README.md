# Quality Studio frontend

Standalone Angular 20 shell for browsing repository quality data. It provides a virtualized hierarchy, virtualized code viewer, review lanes, and shared light/dark theme tokens without a component library.

## Development

```powershell
npm install
npm start
```

For the full product from the repository root, use `npm start`. That command
boots the API and frontend together with the repository-owned launcher, while
the standalone frontend development server still runs at
`http://localhost:4200` and proxies `/api` to the QS API at
`http://127.0.0.1:5127` by default. If the API is unavailable, the shell shows
clearly labeled preview data so the workspace remains inspectable.

Run `npm run build` for the production bundle and `npm run perf` against a running server for the interaction-budget harness. See [PERF.md](./PERF.md) for the acceptance numbers and Chrome tracing procedure, and [DESIGN-KINSHIP.md](./DESIGN-KINSHIP.md) for the Agent Studio token mapping.

## Model / CLI picker

The review-start picker (`qs-model-selector`, in `src/app/model-selector/`) and the
per-repository default model/CLI picker in the repository dialog both consume the
model-selection **contract** owned by `coding-agent-chat` — `ChatCliOption`,
`ChatModelOption`, `ChatModelSelection`, and `shortModelLabel` from
`coding-agent-chat/core` (see [`src/app/quality-api.ts`](./src/app/quality-api.ts)).
QS holds no hardcoded model list of its own.

`coding-agent-chat`'s own Angular component (`<cac-model-selector>` in the
`composer` entry point) targets Angular `>=21 <22`; this workspace is on Angular 20,
so that component cannot be installed here without upgrading. `qs-model-selector` is
therefore a thin, QS-styled Angular 20 component built against the library's
**types only** (the `core` entry point is zero-Angular, pure TS/JS) — same
CLI-pills → model-pills → thinking-level-pills → Done interaction, DESIGN-KINSHIP
tokens instead of the library's own CSS. If/when this workspace moves to Angular 21+,
`qs-model-selector` can be replaced by importing `<cac-model-selector>` from
`coding-agent-chat/composer` directly; the `ChatModelControl`-shaped state already
flowing through `QualityApi` (`cliOptions`, `modelsFor`, `loadModelCatalog`,
`modelCatalogLoading`, `modelCatalogError`) would carry over unchanged.

The catalog itself is served by the API (`GET /api/models`, `GET /api/models/{cliType}`,
see `src/QualityStudio.Api/ModelCatalog.cs`), which in turn sources it from the
`CodingAgentRunner` NuGet package QS already depends on for execution: CLI ids from
`CliTypes`, model ids/vendors from `ModelPriceCatalog.Default`, and per-model
reasoning levels from each driver's `Capabilities(model)`. **Updating the catalog is
a dependency bump, not a QS code change:**

- A `coding-agent-chat` rebuild (new/changed catalog **shape**, e.g. a new
  `ChatModelOption` field) reaches the frontend via `npm install` after the library's
  `dist/coding-agent-chat` is rebuilt (`file:` dependency today; a registry
  `npm install coding-agent-chat@<version>` once the package publishes).
- A `CodingAgentRunner` version bump (new model ids, reasoning levels, or CLIs) reaches
  the API via `dotnet restore` picking up the new `PackageReference` version in
  `src/AgentOrchestrator.CodeQuality/AgentOrchestrator.CodeQuality.csproj`.

A repository's default CLI/model (`RepositoryRegistration.defaultCliType` /
`defaultModel`, editable in the repository dialog) preselects the review-start picker
and is what an omitted `cliType`/`model` on `POST /api/review` falls back to
(`ReviewJobService.Enqueue`), which is what reaches `CliRunRequest.Model` and the
driver's CLI type in `CodingAgentReviewAgent`.

## Workspace layout

The Explorer and Review panel can be collapsed to give the editor more room, and both side panes can be resized by dragging the handle on their border (double-click a handle to reset that pane to its default width). Layout state — which panes are visible and how wide they are — persists in `localStorage` under `qs-layout`, separate from the `qs-theme` key.

Keyboard shortcuts:

- `Ctrl+B` — toggle the Explorer
- `Ctrl+Alt+B` — toggle the Review panel
