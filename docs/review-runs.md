# UI review runs

Quality Studio starts Runner reviews through the API host. The browser never launches an agent process. `POST /api/review` (or the repository-scoped equivalent) validates the selected hierarchy node, records its descendant file plan, enqueues the work, and immediately returns `202 Accepted` with a run ID.

## Preflight estimate

Before confirmation the browser calls `POST /api/review/estimate` (or the repository-scoped equivalent) with the selected CLI, model, kind, path, and cap. The response is computed from the exact file and aggregate prompts that `ReviewRunner` will send. Rendered prompt characters are converted to input tokens at four characters per token. The output/input ratio comes from matching operations in the project's token ledger under `usage/` in its data root ([`data-root.md`](data-root.md)), falling back to 20% when there is no history. The response identifies its history sample count and method, so a fallback is never presented as measured precision.

Cost is computed by `ReviewPriceCatalog.Default` for the selected model. An unknown or currently unpriced model returns an explicit price status and no cost. A cost cap is rejected in that case; a token cap remains available.

Every completed run compares its preflight estimate with usage actually recorded by that sweep. `deviation.inputTokensPercent`, `deviation.outputTokensPercent`, and, when priced, `deviation.costPercent` are signed percentages (positive means actual was higher), and the run row displays them. This is the acceptance comparison against the recorded sweep, not a precision claim: CLI-added system context, provider tokenization, cache behavior, and response length are not knowable from prompt characters. Capped or failed partial runs do not publish a misleading full-sweep deviation.

The server-side acceptance test exercises a two-file plus aggregate sweep through the HTTP API, records each operation in `usage/`, deliberately crosses a token cap, verifies the reviewed/skipped report, raises the cap, and verifies that the completed run reports a non-zero estimate deviation. The assertion intentionally checks deviation rather than equality.

## Freshness, caps, and execution

The API runs uncapped file reviews with bounded concurrency (`ReviewJobs:MaxConcurrency`, default `2`). Capped runs execute serially so concurrent files cannot all cross the boundary. Container sweeps continue after individual file failures and write the selected project, module, or namespace review after all file attempts finish.

Before every file and aggregate operation, the runner compares the current subject manifest hash, effective review-inputs hash, and requested model with the existing sidecar. The comparison reads the sidecar's `reviewer.model`, which every schema version carries. `review-meta.v3` additionally records what was asked for as `reviewer.requestedModel` and `reviewer.requestedThinkingLevel`, described in [`concept.md`](concept.md#review-meta-schema-v3). A unit is `skipped-fresh` only when all three match, and the agent is not called. The run snapshot and durable status include these skips; aggregate freshness is exposed through `aggregateState`. Set `force: true` on the start or estimate request to bypass this gate for every unit in the run.

A run may use one token cap or one cost cap. Omitting both inherits the repository's default. Enforcement happens in `ReviewJobService` at durable review-operation boundaries: once recorded usage reaches the cap, no next file or aggregate operation starts. The operation that crosses the threshold is allowed to finish cleanly, so actual spend can exceed the cap by at most that operation. Remaining files are persisted as `skipped`, the aggregate is reported as `skipped` when applicable, and the run ends as `capped` with a stop reason and complete reviewed, failed, and skipped counts.

### Answers the parser refuses

Every review prompt carries the answer contract,
[`review-response.v1.schema.json`](../schemas/review-response.v1.schema.json), after the output
format section of its template. The runner reads the **first complete JSON object** in the answer
(`AgentJsonReader`): it walks from an opening brace to the brace that closes it, treating everything
inside JSON strings as data, so a recommendation that quotes a fenced snippet no longer breaks the
read, and prose or a second block after the object is ignored. A `json` fence is only a hint where to
start. The schema sits outside the template on purpose: it states the shape the parser already
enforced, so it is not part of the template hash and adding it made no stored review stale.

When the parser still refuses an answer, the same prompt is sent **once more** with the refusal
reason appended ("Your previous answer was rejected"). Both runs are recorded in the usage ledger. A
second refusal fails the unit with the reason and a note that the repair attempt was refused too.
Each refused answer is appended to `runs/<runId>/rejections.jsonl` with the agent run id, attempt,
whether it was retried, the reason, the unit, and the raw answer capped at 32,768 characters (head
and tail, with a marker and the original length when cut). The one-line file error says why an
answer was refused; the journal shows what the agent actually returned.

The CodingAgentRunner 0.7 request has no structured-output channel, so the schema is requested in
the prompt and enforced by the parser. When the runner gains a native schema parameter, the same
embedded schema is what it should pass.

### Provider and login failures

A failed operation is classified before the sweep moves on. Only a failure of the agent run itself
counts — a CLI that ended without completing, failed to attach, or threw while streaming; a refused
answer, an edited file, or an unreadable path does not. A failure is an **authentication** failure
when its message says so (`401`/`403`, unauthorized, not logged in, expired or refreshed token,
invalid API key, credentials); otherwise it is a provider failure. Run ids, GUIDs and timestamps are
removed from the message, so the same fault on two files compares equal. To make that message
available, `CodingAgentReviewAgent` carries the last failed turn or error diagnostic into the abort
reason when the CLI's terminal event does not say why.

After `ReviewJobs:ProviderFailureStopThreshold` (default `3`) **consecutive identical** failures the
run stops: it ends as `failed` with a stop reason naming the CLI and the provider's message, every
file not yet started is persisted as `skipped` with that reason, in-flight operations are cancelled
and also recorded as `skipped`, the aggregate is `skipped`, and the terminal report is published.
Any operation that reached the provider — a review written, or an answer refused — restarts the
count, as does a different failure. The run is not resumable; starting the review again is cheap
because every file already reviewed is `skipped-fresh`.

The same evidence feeds the provider login state that `GET /api/quotas` returns in `auth` and the
top bar shows next to each provider's quota: `signed in` once a review reached the provider,
`auth failed` after an authentication failure (from a review, or from the quota probe's own error
when no review has succeeded since), otherwise `auth unknown`. A provider with a login state but no
quota chip still gets its own badge, so a refused login is never hidden. The state is in memory and
starts as unknown after an API restart.

A capped run is resumable without repeating completed files. `POST /api/review/runs/{id}/resume` accepts a higher `{ "tokenCap": ... }` or `{ "costCap": ... }`. Skipped units return to `queued`, while done and failed units remain durable. The server rejects a replacement cap already below current spend. Repository defaults are configured with `defaultReviewTokenCap` or `defaultReviewCostCap` (mutually exclusive) in the repository registration UI or API.

## Module and project passes

A container run reviews every descendant file first and then writes the selected
project, module, or namespace review as one further operation of the same run.
The aggregate operation therefore reads the file sidecars that run has just
written, and a member reviewed a moment ago appears in it with its new grade.

Module and project reviews have their own prompt templates. `ReviewPromptBuilder`
resolves `(level, kind)` against the embedded templates, so a module `code`
review runs `module-code-review` and a project `security` review runs
`project-security-review`. A level and kind with no template of its own falls
back to the file template - `performance` at every aggregate level, and
`namespace` and `function` everywhere - and `reviewInputs.prompt.id` records the
template that actually ran rather than the one the level would like to have. The
template hash flows into `reviewInputs.effectiveHash` as before, so changing an
aggregate prompt makes aggregate sidecars policy-drift without touching file
sidecars.

The aggregate code prompts ask for the aspects `architecture`, `structure`,
`boundaries`, `duplication` and `consistency`. The aggregate security prompts are
a threat analysis: entry points grouped by trust level, authorisation chains from
an entry point to process, filesystem, network, secret or evaluation surfaces,
trust boundaries, and the sensor-backed secrets and dependency picture. They
close with a plan. The plan needs no new schema field: each finding's
`recommendation` ends with a line of the form `Priority: P1 | Effort: M`, and the
`summary` ends with the mitigation titles in execution order.

### The subject digest

An aggregate review does not receive the concatenated source of its members. It
receives a digest built by `AggregateSubjectDigest` within a character budget of
80,000 by default:

- a header with the member count, total lines and characters, and how many
  members currently have a review of this kind;
- one table row per member with its path, size, owning derived unit, current
  grade and finding counts by severity, read from the file sidecars through
  `ReviewMetaJson`;
- the paths excluded from the aggregate, with their reasons;
- the derived module and namespace structure, passed in by the planner from the
  hierarchy it already holds, so the runner never derives the hierarchy twice;
- the findings already recorded for the members, each with the fingerprint an
  aggregate finding can cite;
- for a security pass, the derived boundary inventory from
  `boundaries/inventory.json` in the data root, scoped to the members at module level and
  whole at project level, or an explicit statement that none has been scanned;
- real source under the remaining budget, at least a quarter of it. Each member
  is allocated a share proportional to its size with a floor, so a monolith
  cannot crowd out its neighbours. A member whose text fits its share is included
  whole; a larger one is reduced to its declaration lines. Every line carries its
  real one-based number, so a range the agent cites is a range in the file.

The digest changes what the agent reads, not what the run considers current.
`reviewedHash` remains the manifest of member subject hashes and aggregate
control files defined in [`concept.md`](concept.md#exact-hashing-contract).

### Cross-file findings

An aggregate review reports a defect class once, with every occurrence in its
`locations` array. The runner anchors each occurrence it can resolve: the first
becomes the `primary` anchor as in a file review, and every further one becomes a
`related` anchor with its own captured excerpt and hashes, plus an `observed`
`sourceSpan` evidence item pointing at it.

A finding may cite the member-file findings it generalises. The agent lists their
fingerprints in `relatedFindings`; the runner checks each against the member
sidecars the digest read, records it as a `legacyClaim` evidence item whose
status is `observed` when it matched and `unverified` when it did not, and
removes the array before writing. `review-meta.v3` findings allow no additional
property, and none is needed.

## Prompt transport

Every file and aggregate prompt reaches the reviewer CLI over standard input, never
on the command line. A file prompt is its template plus the whole file, and an
aggregate prompt carries a digest of up to 80,000 characters, so both routinely
exceed what an operating system accepts as a process argument:

| OS | Limit on a prompt passed as an argument | Consequence |
| --- | --- | --- |
| Windows | `CreateProcess` accepts a whole command line of at most 32,767 characters; through `cmd.exe` the limit is 8,191 | A file of roughly 20 KB plus its template, or any aggregate digest, cannot launch |
| Linux | One argument may be at most 128 KiB (`MAX_ARG_STRLEN`); all arguments plus the environment share `ARG_MAX`, usually 2 MiB | Very large file prompts fail with `E2BIG` |
| macOS | All arguments plus the environment share `ARG_MAX`, 1 MiB | Only extreme prompts fail |

Standard input has no such limit: the runner writes the prompt to the child's pipe
and closes it. It also keeps the full source out of process listings such as `ps`
and `/proc/<pid>/cmdline`.

`CodingAgentReviewAgent.CreateCliOptions()` is the default `CodingAgentRunner`
configuration of the review agent. It sets `ClaudePromptTransport.Stdin`, because
the library's Claude default is still `Argv`. Codex needs no setting: the runner
always launches `codex exec` with `-` and writes the prompt to stdin. A caller that
passes its own `CliOptions` owns the choice, so derive them with
`CodingAgentReviewAgent.CreateCliOptions() with { ... }` to keep stdin.

Before this was set, every folder-level review on Windows and 23 of 124 file
attempts above about 20 KB failed to launch in the 2026-09-28 Agent Studio
evaluation (defect D1). `CodingAgentReviewAgentLargePromptTests` guards against a
regression on both CI legs, ubuntu and windows. It launches the fake CLI in
`backend/tests/TestSupport/FakeCodingAgentCli` as `claude` and as `codex` with a
100 KiB multi-line prompt. The fake takes its dialect from its file name, as the
real CLIs do, so the test runs a copy named `claude[.exe]` or `codex[.exe]`; the
argv-less `--version` probe therefore answers as the CLI under test, which a second
test asserts. The large-prompt test asserts three things: the prompt arrived intact at
the start of stdin, it does not appear in argv, and argv stays below cmd.exe's
8,191 characters. The runner appends its own subagent-delegation note after the
prompt, so stdin is slightly longer than the prompt itself.

## Durable state

Run orchestration is durable under `runs/<runId>/` in the project's data root:

- `manifest.json` is the immutable enqueue-time plan. It records the selected node and level, kind, model, CLI type, force flag, preflight estimate, initial cap, aggregate controls, and every target file with its subject hash.
- `progress.jsonl` is an append-only file-transition log. Each flushed line records the run and file path, state, timestamps, and any error. Recovery ignores an incomplete line left by a crash and continues from the other records.
- `status.json` is the current overall state and its counters, cursor, timestamps, errors, usage, live cost, current cap, aggregate state, and stop reason. It is replaced atomically through a same-directory temporary file.
- `result.json` is the stable evidence projection refreshed atomically with run state. It
  records the chosen `model`, `thinkingLevel`, and `cli` (using explicit
  `runner-default` / `model-default` markers when no override was chosen), together with
  scope, outcome, counts, usage, and cost status for downstream Token Economy analysis.
- `observations.json` is the orchestration checkpoint for exact file and aggregate
  observations. It is replaced atomically before the corresponding progress
  transition is appended, allowing recovery to publish the same captured evidence.
- `rejections.jsonl` is present only when the parser refused an agent answer. Each line keeps one
  refused answer, capped, with its reason; see [Answers the parser refuses](#answers-the-parser-refuses).

At every terminal transition the API projects these immutable inputs into
`reports/runs/<runId>.json`, again in the data root. This canonical,
strict-schema snapshot is the durable history of that execution. A capped run
publishes revision 1; resuming and finishing that run publishes a higher revision
without repeating already completed operations. Renderers, API downloads, CLI gates, and run trends all consume this
snapshot rather than mutable current sidecars. See
[`quality-reports.md`](quality-reports.md) for formats and trend semantics.

Model options come from the governed Token Economy snapshot described in
[`model-catalog-integration.md`](model-catalog-integration.md). The model and optional
thinking-level override are persisted in the manifest before enqueue and passed to the
same CodingAgentRunner request used for every file and aggregate operation.

At startup the API scans the registered repositories for durable runs. `queued` and formerly `running` runs are enqueued again; a file recorded as `done`, `failed`, or `skipped-fresh` is not reviewed again. A file that was `running` when the process stopped is returned to `queued`, because its sidecar write cannot be assumed to have completed. `paused` runs are restored but remain idle. Terminal `done`, `failed`, `cancelled`, and `capped` runs are loaded into recent history without being resumed.

The UI polls `GET /api/review/runs` every 1.5 seconds only while a run is queued or running. Each operation's recorded input/output usage is priced and persisted immediately, so the run row shows live tokens or cost spent against the cap. A terminal transition refreshes the hierarchy and the open file, so sidecar grades and staleness decorations update without a page reload. `POST /api/review/runs/{id}/pause` stops active work at the cancellation boundary while preserving completed files. Repository-scoped forms of all routes are also available. `DELETE /api/review/runs/{id}` permanently cancels queued, paused, or active work.

None of this is in the reviewed checkout and none of it is versioned: run
journals are disposable orchestration working data, and even the canonical
reports are reproducible output of a run rather than repository history — keeping
them in the tree is what made the studio's own checkout permanently dirty. Review
sidecars remain the current-state truth and canonical run reports the historical
truth of each terminal execution, both in the project's data root
([`data-root.md`](data-root.md)). A report meant to be shared is exported
deliberately with `quality report --output`.
