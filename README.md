# Quality Studio

**The engineer room of the Agent Orchestrator universe: agent-driven, layered code reviews with quality truth persisted next to the code.**

Part of the [Agent Orchestrator](https://agent-orchestrator.dev) universe — alongside
Agent Studio (the cockpit), Runner (executes), Coding Agent Chat
(converses), and Token Economy (accounts). Quality Studio is the room you step into when you wear the engineer hat — the one that **reviews**.

> Working state, 2026-08-04: the core library, the `quality` CLI, the review API
> and the Angular browser all ship from this repository and are covered by the required CI gate;
> cards through QS-52 are delivered. No package is published to NuGet yet.
> Product URL will be `agent-orchestrator.dev/quality`; the proposed final
> core package ID and root namespace are `AgentOrchestrator.CodeQuality` (subject
> to a release-time ownership/availability recheck); formal long name: Agent
> Quality Studio. The detailed v1 contracts live in [`docs/concept.md`](docs/concept.md).

## What this is — and what it is not

This is **not static code analysis**. Coding agents read, judge, and grade the code —
orchestrated across review kinds and abstraction levels — and their findings become
versioned, repo-owned facts. You work *with* agents on quality; the tool orchestrates
them and keeps the ledger honest.

## The concept

### 1. Levels, not one blob

Quality statements exist per level of a hierarchy and are never aggregated away:

```
Project → Module → Namespace → File → Function
```

A file review, a module review, and a project review are *different statements*.
Sweeps run over a whole project per **review kind**: `code`, `security`, and
`performance` (security is designed as a detachable module — it can grow into its
own thing). Architecture is a project/module code-review aspect in v1, not a
fourth kind.

### 2. Review metadata lives next to the code (the heart)

Every reviewed unit gets a small structured JSON meta file **in the same feature
folder** as the code it describes:

- `reviewedAt` — when the last review ran
- `kind` — code / security / performance
- `findings[]` — structured findings
- `grade` — the level's grade
- `reviewedHash` — hash of the exact content that was reviewed

The hash makes staleness self-evident: if the code has moved on, the review visibly
no longer applies. History comes for free via Git. The repository owns its quality
truth — diffable, portable, reviewable like any other artifact.

Relationship to task-time reviews in Agent Studio: a task review is a **snapshot of a
diff**; Code Quality is the **standing truth of the codebase**.

### 3. Product shape

- **Core as a package** (`AgentOrchestrator.CodeQuality`): hierarchy model,
  meta-file schema, staleness logic, sweep planning — pure and testable.
- **API**: trigger sweeps, read the quality state, manage review runs.
- **Frontend**: its own surface in the Studio style, reusing the shared component
  family (tabs, panels, conversation components) — primarily the companion's own
  development and inspection tool.
- **Handover to Agent Studio (decided direction):** the integration points the OTHER way.
  Quality Studio calls Agent Studio: from any review finding you trigger a handover -
  "make this a task" - and a card is created through the normal task mutation path.
  Agent Studio needs no quality surfaces; Quality Studio is the engineer room, and its
  exit is a task.

### 4. Neighbors in the universe

- Project graph may consume and visualize the hierarchy's derived upper levels;
  workspace/solution/compiler structure remains the source of truth.
- Style-guide layer supplies the per-technology rules that reviews check against.
- Retro-grading and the remote review pipeline of Agent Studio are execution paths.

## The core interaction: augmented code browsing

The role reversal that clarifies everything: in Agent Studio you work at
feature level - code is an artifact rushing past. Here you come **as an engineer**
and want to see the quality characteristics of what was built.

- **The code browser is the center.** Folder structure and feature folders up front;
  enter anywhere (project -> subproject -> folder -> file). On top of everything sits
  the meta layer: grades per kind (code, security, **performance**), staleness at a glance.
- **File level reviews are split into aspects** - never a blanket good/bad, but named
  finding strands, augmented directly in the editor view.
- **Input management:** review standards defined globally, overridable per project
  (style guides, rules, thresholds).
- **Hard performance goals:** a rock-solid, extremely fast editor view (file-level
  augmentation at the code, not beside it) and a tree that is keyboard-driven, has a
  context menu, loads files instantly, and follows the Git state.
- **Research box (open on purpose):** whether a code graph joins as a graphical meta
  layer is a research topic, not a pre-decision.
- The package stays usable standalone (iterate code over code: write meta JSONs,
  drive the CLI runner). Review execution runs through Runner;
  finding handover uses Agent Studio's normal task mutation path.

## Status

- [x] Repository founded, concept anchored (this README)
- [x] QS-1: concept elaboration — review-meta schema, derivable hierarchy,
      staleness, package naming, handover contract, augmented-browser requirements,
      review inputs, website outline, and honest QS-2…QS-13 slice plan
      ([`docs/concept.md`](docs/concept.md))
- [x] Scaffold (package, CI, release rails — Token Economy pattern)
- [x] Core library, `quality` CLI, review API, and Angular browser shipping
      from this repository (cards through QS-52); nothing published to NuGet yet

## Staleness scan

The `quality` CLI computes the current file-review state without rewriting review
metadata. It respects `.gitignore`, hashes content only when a matching sidecar
exists, and returns exit code `1` when any review is stale (`2` for scan errors).

```shell
dotnet run --project src/quality-cli -- scan . --include "**/*.cs"
```

The default globs cover common programming and web source extensions. Repeat
`--include` to replace them with a custom set, or select a sibling review kind
with `--kind security` or `--kind performance`.

## Boundary inventory

Derive the repository's externally callable, host, browser, process, filesystem,
and caller-influenced outbound surfaces and run the standard mechanical checks:

```shell
dotnet run --project src/quality-cli -- boundaries scan .
```

The stable result is written to `.quality/boundaries/inventory.json`, so boundary
changes appear in normal source-control diffs. See
[`docs/boundary-inventory.md`](docs/boundary-inventory.md) for the contract and
derivation rules.

## Change-set review

Review one merge range, or backfill an integration trajectory, without sweeping
untouched units:

```shell
dotnet run --project src/quality-cli -- diff . --base <base> --head <head> --fail-on-regression
dotnet run --project src/quality-cli -- diff . --last 20
```

Change truth is committed under `.quality/changes/`. See
[`docs/change-reviews.md`](docs/change-reviews.md) for provider semantics,
deterministic delta fields, agent aspects, economy measurements, and gate exit
codes.

## Security scan

Run the deterministic Gitleaks sensor to produce structured security findings and
repository-owned security review sidecars:

```shell
dotnet run --project src/quality-cli -- security scan .
```

Use `--mode range --range main..HEAD` for a commit range or `--mode staged` for
the staged candidate snapshot. The scanner is pinned and verified; if it cannot
be resolved, the command reports an explicit unavailable state instead of a
false pass.

## Deterministic analyzer evidence

Repository-configured Roslyn, ESLint and TypeScript diagnostics, plus producer-neutral
SARIF 2.1.0, are supplied to the review agent as prior facts while staying separate
from its findings and grade. Configuration and unavailable behavior are documented in
[`docs/deterministic-analyzer-evidence.md`](docs/deterministic-analyzer-evidence.md).

## Review inputs

Global and repository-owned Markdown guidelines can be resolved into review prompts with deterministic overrides and an explicit size budget. See [`docs/review-inputs.md`](docs/review-inputs.md) for the `.quality/inputs/` convention and `--explain-inputs` usage.

## Review usage telemetry

Agent-backed reviews persist their model, CLI, token counts, duration, and run
identity both with the review truth and in a repository-local append-only ledger.
The API exposes repository usage aggregates and provider quota availability. See
[`docs/usage-telemetry.md`](docs/usage-telemetry.md) for the versioned storage
contracts, endpoint semantics, quota source of truth, and unavailable behavior.

Review model selection is governed by the synchronized Token Economy routing and price
catalogs, including capability tiers, supported thinking levels, and retirement status.
See [`docs/model-catalog-integration.md`](docs/model-catalog-integration.md) for the
package-vs-snapshot decision, drift check, picker rules, and run evidence artifact.

## Quality reports

Export the project scorecard, Git-backed score trend, findings, coverage, sensor
posture, and registry comparison as Markdown, HTML, JSON, or SARIF:

```shell
dotnet run --project src/quality-cli -- report . --format sarif --output quality-report.sarif
dotnet run --project src/quality-cli -- report . --run <run-id> --format html --output quality-run.html
```

Run-scoped exports render the exact terminal snapshot captured under
`.quality/reports/runs/`; they do not re-read mutable review sidecars. CI gates
use `--fail-under <score>` and `--fail-on <severity>`. See
[`docs/quality-reports.md`](docs/quality-reports.md) for report semantics,
endpoint formats, and documented exit codes.

## Repository layout

- `src/AgentOrchestrator.CodeQuality/` contains the core quality model library.
- `tests/AgentOrchestrator.CodeQuality.Tests/` contains its xUnit test suite.
- `.github/workflows/build.yml` builds and tests the solution for pushes and pull requests to `main`.

## Required test baseline

The pull-request gate pins .NET 10.0.301 and Node 22.23.1, restores the committed
lock files, keeps machine-bound timing checks out of routine test runs, builds the
production Angular bundle under its unchanged 480 kB budget, provisions the
Playwright Chromium version declared by the frontend lock file, and runs Angular,
dev-stack, coverage, and pinned Gitleaks checks. The equivalent local commands are:

```shell
export COVERAGE_ROOT="${TMPDIR:-/tmp}/quality-studio-coverage"
dotnet restore QualityStudio.slnx --locked-mode
dotnet build QualityStudio.slnx --configuration Release --no-restore
dotnet test tests/AgentOrchestrator.CodeQuality.Tests/AgentOrchestrator.CodeQuality.Tests.csproj --configuration Release --no-build --filter "Category!=MachineBound" --collect:"XPlat Code Coverage" --results-directory "$COVERAGE_ROOT/core"
dotnet test tests/QualityStudio.Api.Tests/QualityStudio.Api.Tests.csproj --configuration Release --no-build --filter "Category!=MachineBound" --collect:"XPlat Code Coverage" --results-directory "$COVERAGE_ROOT/api"
npm run test:dev-stack
cd frontend
npm ci
npm run browser:install
npm run test:browser-resolver
npm run build
COVERAGE_DIR="$COVERAGE_ROOT/frontend" npm run test:coverage
cd ..
npm run coverage:check -- --cobertura core="$COVERAGE_ROOT/core" --cobertura api="$COVERAGE_ROOT/api" --lcov frontend="$COVERAGE_ROOT/frontend/lcov.info"
dotnet run --project src/quality-cli --configuration Release --no-build -- security provision
dotnet run --project src/quality-cli --configuration Release --no-build -- security scan .
```

Set `CHROME_NO_SANDBOX=1` only on a controlled Linux runner that cannot use the
Chromium sandbox. Coverage is generated as Cobertura for the two .NET test projects
and lcov for Angular. `.quality/coverage-baseline.json` records the first measured
project and feature-area line rates; `npm run coverage:check -- ...` rejects missing,
unreadable, or regressed reports.

Tests carrying `Category=ToolBound` intentionally exercise Git, .NET, or a pinned
native tool on a provisioned PR host. Tests carrying `Category=MachineBound` contain
host timing or performance assertions and run only in the labeled release canary.
That canary retains three samples, host metadata, JSON, screenshots, and TRX output;
the optional live-agent check is enabled manually only for review-execution changes.

## Minimal API

The ASP.NET Core host provides repository tree, file/meta overlay, staleness scan,
and optional review-trigger endpoints. See [`docs/api.md`](docs/api.md) for
configuration and live curl examples.

## One-click dev stack

Project Hub should start Quality Studio through the repository-owned launcher, not
as two separate services. The repository-owned start rule is:

```powershell
npm start
```

`npm start` boots the API and frontend together, bootstraps the frontend
dependencies on a clean checkout, waits for `GET /health` and the Angular shell,
and prefixes the child logs so API and web output stay readable. The default
ports are API `5127` and product `4200`, and both can be overridden with
`--api-port` / `--web-port` or `QUALITY_STUDIO_API_PORT` /
`QUALITY_STUDIO_PRODUCT_PORT` when the launcher is invoked from another host.
For alternate checkout layouts and automation, the launcher also accepts
`--repo-root`, `--frontend-root`, and `QUALITY_STUDIO_NPM_COMMAND`. Test harnesses
that invoke npm through a platform-neutral Node stub can provide its leading
arguments as a JSON string array in `QUALITY_STUDIO_NPM_COMMAND_ARGUMENTS`.

The shell distinguishes `Repository connected`, `API offline · preview data`,
and `API offline` states so embedded review flows do not pretend the API is live
when it is not.

## License

Apache-2.0 — see [LICENSE](LICENSE).
