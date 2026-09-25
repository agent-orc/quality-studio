# Quality Studio change history

Product-level history. The rule library keeps its own in [`rules/CHANGELOG.md`](rules/CHANGELOG.md).

## Unreleased

### Added — September 2026 model prices (QS-104)

- Synchronized TokenEconomy prices and routing for Claude Opus 5.5, GPT-6 Sol, and GPT-6 Luna, effective 2026-09-22. The model picker exposes them for explicit reviews. Claude Code 2.1.281 is the observed minimum for Opus 5.5; Codex CLI 0.155.0 is the observed minimum for Sol and the discovery minimum for Luna. Luna execution remains unverified.
- Usage queries now reprice historical v3 ledger entries saved with `unknownModel` when a catalog price becomes available. The original ledger lines remain append-only.

### Fixed — Claude usage accounting, caps and cost estimates (QS-110)

The ledger dropped Claude's `cache_creation_input_tokens` and priced Claude's cache reads as if
they were a share of the fresh input, so the 2026-09-28 agent-studio-dev sweep recorded USD 12.50
against USD 22.53 billed, token and cost caps saw only part of the input, and the preflight
predicted USD 56.59 for a USD 10.61 run (defects D5-D6).

- The review agent reads cache-write tokens from the Claude CLI's raw `result` line (the runner's
  usage summary drops them). `inputTokens` now counts every input token — fresh, cache read and
  cache write — with `cachedInputTokens` and the new `cacheWriteInputTokens` as subsets, each priced
  at its catalogue rate. Token caps and cost caps therefore count all input.
- Usage ledger schema 4 (`schemas/usage-ledger.v4.schema.json`). Earlier entries are not rewritten:
  pre-schema-4 Claude entries are normalized and repriced on read and flagged with an
  `accountingNote` as under-priced (their cache writes were never captured); `GET /api/usage`
  reports `underPricedRuns` and `cacheWriteInputTokens`, and the usage history shows both.
- The preflight estimate learns only from recorded operations of the same CLI, model and review
  kind, per level; without such history it falls back to the rendered prompt size. The estimate
  carries its `basis` (`history` or `prompt-size`) and cache classes, and the preflight sheet shows
  the basis.

### Added — metrics beyond grades: coverage producer and complexity (QS-115)

- **Coverage producer.** The `coverage` sensor can run a host-owned, time-boxed profile before it
  ingests: `dotnet-test-coverage` (`dotnet test --collect "Code Coverage;Format=cobertura"`, 900 s),
  `vitest-frontend-coverage` and `vitest-root-coverage` (600 s). Opt-in per repository through
  `configuration.profile`; each run writes to `coverage/produced/<profile>/<run-id>/` in the data root, never the
  checkout. A failed producer keeps the last snapshot. Analyzer profiles gained `timeoutSeconds`.
- **Complexity.** Cyclomatic and cognitive complexity per function and file for C# (Roslyn syntax) and
  TypeScript/JavaScript, in every risk row (`complexity`), in a new `GET /api/repos/{repoId}/complexity`
  route, and as a sortable column of the risk view. It feeds the risk score as a 20 % component; files
  without a measurement keep the earlier weights.
- **Directory grade projection.** A directory without its own review shows the line-weighted mean of
  its files' grades, labelled `projection` (`≈C · 79`), with how many of its files it rests on.

### Changed — keep host-dependent tests out of the Windows pre-main gate (QS-120)

The 15-second boundary inventory scale budget is `MachineBound`, matching the
QS-115 change. The fake Codex 100 KiB stdin transport is `MachineBound` only on
Windows, where the QS-97 pre-main gate failed it under load; it remains in the
Linux gate, and the fake Claude transport remains in both gates. The test-lane
guideline now classifies timing budgets and host-dependent live-process transports.

### Changed — review output contract, finding identity and failure handling (QS-109)

From the 2026-09-28 evaluation of the Agent Studio checkout (defects D2-D4):

- **Answers are read, not matched.** The first complete JSON object in an agent answer is read with a
  string-aware reader instead of a lazy fence regex, so valid JSON whose strings contain a code fence
  is accepted. Every review prompt carries the new
  [`review-response.v1.schema.json`](schemas/review-response.v1.schema.json). A refused answer is
  retried once with the refusal reason; each refused answer is kept, capped at 32,768 characters, in
  `runs/<runId>/rejections.jsonl`.
- **Findings keep their identity across reruns.** A re-reported finding keeps an earlier identity by
  rule id plus overlapping anchor span on content with the same hash, never by its wording or by the
  text of the code it encloses alone: a finding that matches no earlier anchor gets a fingerprint no
  lifecycle record uses, so it cannot inherit a disposition by text. A finding
  missing from a rerun of unchanged code is `not-reobserved`, never `resolved`; the run report delta
  lists such findings under `notReobserved`.
- **Identical provider failures stop the sweep.** After `ReviewJobs:ProviderFailureStopThreshold`
  (default 3) consecutive identical provider or authentication failures the run ends as `failed`
  with the reason and skips the remaining files. `GET /api/quotas` returns each provider's login
  state in `auth`, and the top bar shows it next to the quota.

### Fixed — large and aggregate reviews launch on Windows (QS-108)

The review agent now hands Claude its prompt over stdin (`ClaudePromptTransport.Stdin`) instead of
as a command-line argument; Codex already used stdin. On Windows the 32,767-character command-line
limit had stopped every folder-level review and files above about 20 KB from launching. Limits
per OS and the regression test are described in [`docs/review-runs.md`](docs/review-runs.md#prompt-transport).

### Changed — the studio no longer writes into the checkout it analyses (QS-102)

Everything a run generates now lives in a per-project **data root** outside the analysed working
copy: `%LOCALAPPDATA%\QualityStudio\projects\<project-key>\` by default, overridable with
`QualityStudio:DataRoot` or `QUALITY_STUDIO_DATA_ROOT`. The container image and `docker-compose.yml`
point it at the `/data` volume that was already reserved for this.

Moved out of the checkout: review sidecars, `findings/`, `reports/`, `usage/`, `boundaries/`,
`coverage/`, `changes/`, `flows/`, `runs/` and the attack coverage ledger. Author-owned inputs stay
where they are and are still versioned — `.quality/scope.json`, `.quality/inputs/`,
`.quality/rules/overrides.json`, `.quality/security/` and `.quality/attacks/catalogue.json` — as does
`.quality/preflight/`, which an external analyzer writes under the working directory it runs in.

Review sidecars changed shape as well as location: instead of a `.quality/reviews` folder beside
every reviewed file, one `reviews/` lane in the data root mirrors the subject's directory.

Why: this repository is both the integration checkout of the QS project in Agent Studio and a
project the studio analyses. Every run left the working tree dirty, Agent Studio refuses to
fast-forward a dirty checkout, and QS-85 failed on that on 2026-08-27. Committing 139 generated
files on 2026-09-06 unblocked nine deliveries but was a workaround, not the rule.

### Added

- `quality migrate-data [path] [--dry-run]` moves an existing checkout's generated `.quality` data
  to its data root. Idempotent, refuses to overwrite what the data root already holds, and leaves
  inputs alone.
- The API warns at startup when a registered project root still carries generated studio data or has
  uncommitted changes below a `.quality` folder — the exact condition that blocks integration.

### Fixed

- The live report no longer grades files that are absent from the checked-out branch. Sidecars used
  to be committed, so a branch switch changed which of them existed; the data root is shared across
  branches, and without this a branch that deleted a file would still be graded on it.

### Removed

- Monthly usage ledgers and `.quality/reports/runs/*.json` are no longer versioned. `.gitignore`
  ignores everything under `.quality` and names the author-owned inputs back in. A report meant to
  be shared is exported deliberately with `quality report --output`.
- `.gitattributes` held one rule, `merge=union` for the in-tree usage ledgers. Those ledgers are no
  longer in the tree, so the file is gone.

### Upgrading

`.gitignore` does not untrack a file that is already committed, so an existing checkout needs one
migration and one commit:

```shell
dotnet run --project src/quality-cli -- migrate-data . --dry-run
dotnet run --project src/quality-cli -- migrate-data .
git commit -am "chore(quality): move generated studio data out of the checkout"
```

Two known consequences, both from features that read sidecars out of Git history and therefore can
only see the layout that history was written in:

- The score trend in `quality report` keeps every point up to the migration commit and gains none
  after it. Live grades are read from the data root and are unaffected.
- A change review over a range whose endpoints are both after the migration reports no agent-grade
  movement. Everything it derives from the diff — touched units, boundary and coverage facts,
  evidence economy — is unaffected, and a range spanning older commits still reads their sidecars.

See [`docs/data-root.md`](docs/data-root.md).
