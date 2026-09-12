# Quality Studio

**The engineer room of the Agent Orchestrator universe: agent-driven, layered code reviews with quality truth persisted next to the code.**

Part of the [Agent Orchestrator](https://agent-orchestrator.dev) universe — alongside
Agent Studio (the cockpit), Runner (executes), Coding Agent Chat
(converses), and Token Economy (accounts). Quality Studio is the room you step into when you wear the engineer hat — the one that **reviews**.

> Working state, 2026-09-06: the core library, the `quality` CLI, the review API
> and the Angular browser all ship from this repository and are covered by CI;
> cards through QS-94 are delivered or salvaged, QS-95 to QS-102 are open. The
> core package, its assembly and its root namespace share one name,
> `AgentOrchestrator.CodeQuality`; CI packs it, publication to NuGet is not yet
> configured. Product URL is `agent-orchestrator.dev/quality`; formal long name:
> Agent Quality Studio. The detailed v1 contracts live in
> [`docs/concept.md`](docs/concept.md); the September 2026 assessment with the
> open decisions lives in
> [`docs/operations/product-assessment-2026-09/`](docs/operations/product-assessment-2026-09/index.html).

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

The hierarchy is derived from workspace files and compiler structure, and every
language needs its own strategy; see
[`docs/hierarchy-derivation.md`](docs/hierarchy-derivation.md) for what each
adapter derives and where it stops.

A file review, a module review, and a project review are *different statements*.
Sweeps run over a whole project per **review kind**: `code`, `security`, and
`performance`. Security is a review workflow of its own inside the same package
and assembly — separate prompts, sensors, runs, grades, and UI state — not a
separate package or repository. Architecture is a project/module code-review
aspect in v1, not a fourth kind.

### 2. Review metadata lives next to the code (the heart)

Every reviewed unit gets a small structured JSON meta file **in the same feature
folder** as the code it describes:

- `reviewedAt` — when the last review ran
- `kind` — code / security / performance
- `findings[]` — structured findings
- `grade` — the level's grade
- `reviewedHash` — hash of the exact content that was reviewed

Writers emit `review-meta.v3`; readers accept v1, v2, and v3. The version table
and the semantics of every v3 field are in
[`docs/concept.md`](docs/concept.md#review-meta-schema-v3), the schema artifacts
in [`schemas/`](schemas/README.md).

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

## Prerequisites

Either run the container image, which carries all of this, or install it on the host:

| What | Why |
| --- | --- |
| **A pre-authenticated agent CLI on `PATH`** — `codex` or `claude` | Reviews are performed by coding agents. Sign in once with the CLI itself; Quality Studio never handles agent credentials and cannot prompt for a login. |
| **.NET 10 SDK** | Builds and runs the API, the core library and the `quality` CLI. |
| **Node 22** | Builds the Angular browser and runs the ESLint/TypeScript analyzer profiles. |
| **git** | The hierarchy, churn and change-set paths start git processes; every registered repository must be a Git working copy. |
| **Gitleaks** (optional) | Provisioned on demand and verified against a tracked digest. Set `QUALITY_GITLEAKS_PATH` to an existing pinned binary to skip the download entirely — see [`docs/security-gitleaks.md`](docs/security-gitleaks.md). |

The container image is the same product on one port: `docker build -t quality-studio:local .`. It hosts
the API and the built browser together, runs as a non-root user and ships git and the pinned Gitleaks
binary. [`docs/deployment.md`](docs/deployment.md) has the run command, the environment variables, and
how Agent Studio gets a token.

## Where results are kept

The studio analyses a checkout; it does not file its results in one. Findings, grades, ledgers, run
reports and review sidecars are written to a per-project **data root** outside the analysed working
copy — `%LOCALAPPDATA%\QualityStudio\projects\<project-key>\` by default, `QualityStudio:DataRoot`
or `QUALITY_STUDIO_DATA_ROOT` to place it elsewhere. Nothing the studio generates is versioned;
`.quality` in a checkout holds only author-owned inputs (`scope.json`, `inputs/`, `rules/`,
`security/`, `attacks/catalogue.json`), and a report meant to be shared is exported deliberately with
`quality report --output`.

A checkout written by an earlier version is migrated once:

```shell
dotnet run --project backend/src/quality-cli -- migrate-data . --dry-run
dotnet run --project backend/src/quality-cli -- migrate-data .
```

See [`docs/data-root.md`](docs/data-root.md) for the contract, the identity rule, what stays in the
checkout and why, and the one commit that makes an already-committed tree clean again.

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
dotnet run --project backend/src/quality-cli -- scan . --include "**/*.cs"
```

The default globs cover common programming and web source extensions. Repeat
`--include` to replace them with a custom set, or select a sibling review kind
with `--kind security` or `--kind performance`.

## Boundary inventory

Derive the repository's externally callable, host, browser, process, filesystem,
and caller-influenced outbound surfaces and run the standard mechanical checks:

```shell
dotnet run --project backend/src/quality-cli -- boundaries scan .
```

The stable result is written to `boundaries/inventory.json` in the project's data
root. See
[`docs/boundary-inventory.md`](docs/boundary-inventory.md) for the contract and
derivation rules.

## Change-set review

Review one merge range, or backfill an integration trajectory, without sweeping
untouched units:

```shell
dotnet run --project backend/src/quality-cli -- diff . --base <base> --head <head> --fail-on-regression
dotnet run --project backend/src/quality-cli -- diff . --last 20
```

Change truth is written under `changes/` in the project's data root. See
[`docs/change-reviews.md`](docs/change-reviews.md) for provider semantics,
deterministic delta fields, agent aspects, economy measurements, and gate exit
codes.

## Security scan

Run the deterministic Gitleaks sensor to produce structured security findings and
repository-owned security review sidecars:

```shell
dotnet run --project backend/src/quality-cli -- security scan .
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

## Rule library

Named, versioned coding-standard rules for code, security, and performance reviews ship with the
product. They are authored as Markdown in [`rules/`](rules/README.md), generated into an embedded
JSON catalogue by `npm run rules:sync`, and resolved into every review that matches their kind and
technology — no per-repository install step. A repository disables or re-weights individual rules
in `.quality/rules/overrides.json`, and `GET /api/rules` returns the resolved catalogue with a
trace per rule.

## Review usage telemetry

Agent-backed reviews persist their model, CLI, token counts, duration, and run
identity both with the review truth and in a project-local append-only ledger.
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
dotnet run --project backend/src/quality-cli -- report . --format sarif --output quality-report.sarif
dotnet run --project backend/src/quality-cli -- report . --run <run-id> --format html --output quality-run.html
```

Run-scoped exports render the exact terminal snapshot captured under `reports/runs/`
in the project's data root; they do not re-read mutable review sidecars. CI gates
use `--fail-under <score>` and `--fail-on <severity>`. See
[`docs/quality-reports.md`](docs/quality-reports.md) for report semantics,
endpoint formats, and documented exit codes.

## In-process analysis package

`AgentOrchestrator.CodeQuality` is the headless package boundary for Agent Studio
pipeline steps, the CLI, and CI hosts that already own a repository checkout.
Run the real CLI proof without starting the API or UI:

```shell
dotnet run --project backend/src/quality-cli -- analyze . --analysis boundaries
```

See the [package README](backend/src/AgentOrchestrator.CodeQuality/README.md) for the
programmatic surface and the
[analysis-core dossier](docs/operations/analysis-core-package/index.html) for
the dependency inventory, standalone consumers, and repository-extraction
criteria.

## Repository layout

Backend and frontend each own their source code and tests. Repository-wide contracts,
documentation, rule content and development tooling stay at the root:

```text
backend/
  src/
    AgentOrchestrator.CodeQuality/  # publishable .NET analysis package
    QualityStudio.Api/             # ASP.NET Core host
    quality-cli/                   # command-line host
  tests/                          # .NET test projects and shared fixtures
frontend/
  src/                            # Angular application and unit tests
  tests/                          # browser integration and performance checks
tests/                            # repository tooling tests and coverage baseline
scripts/                          # launcher, catalogue synchronization and measurements
rules/                            # authored review rules
schemas/                          # shared versioned data contracts
samples/                          # contract examples
docs/                             # architecture, API, operations and visual standards
QualityStudio.slnx                # root entry point for every .NET project
Directory.Build.props             # shared .NET build settings
```

Run `dotnet build QualityStudio.slnx` and the named test lanes documented below from the
repository root. Run Angular commands from `frontend/`; `npm start` at the root starts
the complete development stack. Generated `bin/`, `obj/` and local server state remain
ignored and are never part of the source layout.

See the [frontend architecture](frontend/README.md), [review-rule library](rules/README.md),
and [visual standard](docs/operations/style-guide/index.html) for their conventions.
`.github/workflows/build.yml` validates the full repository; `Dockerfile`,
`.dockerignore` and `docker-compose.yml` build and run the single-container host.

## Required test baseline

The required gate runs the .NET tests as named lanes instead of one undifferentiated
selection. `scripts/test-lanes.mjs` defines every filter once;
`scripts/run-dotnet-lane.mjs` inventories each expected test project before it runs and
fails when a selection is empty, so a filter typo cannot pass as a green run. The
equivalent local commands are:

```shell
dotnet restore QualityStudio.slnx --locked-mode
dotnet build QualityStudio.slnx --configuration Release --no-restore
npm run test:repository-contracts
node scripts/run-dotnet-lane.mjs portable --configuration Release --no-build
node scripts/run-dotnet-lane.mjs tool-bound --configuration Release --no-build
node scripts/run-dotnet-lane.mjs non-machine --configuration Release --no-build --coverage
npm run test:dev-stack
cd frontend
npm ci
npm run test:browser-resolver
npm run build
npm run test:coverage
```

Uncategorized xUnit tests are portable. Tests carrying `Category=ToolBound`
intentionally exercise Git, .NET, a browser, or a pinned native tool on a provisioned
PR host. Tests carrying `Category=MachineBound` contain host timing or performance
assertions and run only in the labeled release canary. `Category=ExternalLive` is
selected only by explicit canary approval; without its opt-in environment it fails
rather than skipping, so `--filter "Category!=MachineBound"` on its own is not the
required selection.

Before requesting review, `npm run test:pre-review` gives a quick deterministic signal:
repository contracts, one Release build, both portable .NET project selections, and
browser prerequisite resolution. It does not claim gate equivalence - the required gate
still owns tool-bound, host-integration, production Angular, coverage, and security
evidence. See
[`docs/operations/test-baseline/keep-green.md`](docs/operations/test-baseline/keep-green.md)
for fixture ownership, lane-change rules, and the honesty contract.

## Minimal API

The ASP.NET Core host provides repository tree, file/meta overlay, staleness scan,
and optional review-trigger endpoints. See [`docs/api.md`](docs/api.md) for
configuration and live curl examples, and [`docs/deployment.md`](docs/deployment.md)
for running it as one container that also serves the browser.

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
