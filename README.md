# Quality Studio

**The engineer room of the Agent Orchestrator universe: agent-driven, layered code reviews with quality truth persisted next to the code.**

Part of the [Agent Orchestrator](https://agent-orchestrator.dev) universe — alongside
Agent Studio (the cockpit), Coding Agent Runner (executes), Coding Agent Chat
(converses), and Token Economy (accounts). Quality Studio is the room you step into when you wear the engineer hat — the one that **reviews**.

> Working state, 2026-07-11: repository founded from the operator-approved concept
> below. Product URL will be `agent-orchestrator.dev/quality`; the proposed final
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

The role reversal that clarifies everything: in Agent Orchestrator you work at
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
  drive the CLI runner). Review execution runs through Coding Agent Runner;
  finding handover uses Agent Studio's normal task mutation path.

## Status

- [x] Repository founded, concept anchored (this README)
- [x] QS-1: concept elaboration — review-meta schema, derivable hierarchy,
      staleness, package naming, handover contract, augmented-browser requirements,
      review inputs, website outline, and honest QS-2…QS-13 slice plan
      ([`docs/concept.md`](docs/concept.md))
- [ ] Scaffold (package, CI, release rails — Token Economy pattern)

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

## Review inputs

Global and repository-owned Markdown guidelines can be resolved into review prompts with deterministic overrides and an explicit size budget. See [`docs/review-inputs.md`](docs/review-inputs.md) for the `.quality/inputs/` convention and `--explain-inputs` usage.

## Review usage telemetry

Agent-backed reviews persist their model, CLI, token counts, duration, and run
identity both with the review truth and in a repository-local append-only ledger.
The API exposes repository usage aggregates and provider quota availability. See
[`docs/usage-telemetry.md`](docs/usage-telemetry.md) for the versioned storage
contracts, endpoint semantics, quota source of truth, and unavailable behavior.

## Repository layout

- `src/AgentOrchestrator.CodeQuality/` contains the core quality model library.
- `tests/AgentOrchestrator.CodeQuality.Tests/` contains its xUnit test suite.
- `.github/workflows/build.yml` builds and tests the solution for pushes and pull requests to `main`.

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
`--repo-root`, `--frontend-root`, and `QUALITY_STUDIO_NPM_COMMAND`.

The shell distinguishes `Repository connected`, `API offline · preview data`,
and `API offline` states so embedded review flows do not pretend the API is live
when it is not.

## License

Apache-2.0 — see [LICENSE](LICENSE).
