# Model catalog integration

Quality Studio treats Token Economy's model-routing policy and price catalog as the
authority for review model identity, capability, thinking levels, and routing status.
The review UI does not maintain a second hand-written model list.

The embedded snapshot is Token Economy **v0.3.9**, tagged commit
`c8de755d31c806960ab81bc77ba19f9842c1c522`, with policy and evidence date
**2026-11-01**. QS-136 already advanced this checkout beyond QS-121's requested
v0.3.7 (`61f2fa3eda6bf15e98ac02ab54ff9d95bbe2ae36`, policy date 2026-10-04).
QS-121 retains v0.3.9 so later model additions, dated tariffs, prompt-size tiers,
and the Sonnet 5.5 cache-read correction remain available. Synchronization and
upstream integrity checks use the clean v0.3.9 checkout; all catalogue hashes
already match that tag, so no catalogue content or manifest hash changes are needed.

## Integration decision

The preferred long-term integration is a `TokenEconomy` .NET package reference. The
project is packable, but the currently published `0.2.0` package predates
`ModelRoutingKnowledgeBase` and does not contain the required routing-policy API or
embedded `model-routing-policy.json`. Consuming that package would therefore expose
pricing but not the required capability and retirement facts.

Until a package containing the routing knowledge base is released, Quality Studio uses
the defined snapshot path:

- `token-economy-model-routing-policy.json` and `token-economy-model-prices.json` under
  `backend/AgentOrchestrator.CodeQuality/catalogues/` are exact Token Economy files;
- `token-economy-model-catalog.snapshot.json` records the upstream repository, commit,
  policy version, and SHA-256 for both files;
- `ReviewModelCatalog` reads only these embedded files and joins routing facts with price
  availability for the API; and
- `npm run catalog:check` verifies snapshot hashes and the routing/price identity join in
  CI. Supplying a Token Economy checkout also performs the upstream drift comparison.

Refresh and verify the snapshot from a clean Token Economy checkout:

```shell
npm run catalog:sync -- --source ../token-economy
npm run catalog:check -- --source ../token-economy
```

`TOKEN_ECONOMY_REPOSITORY` is the equivalent automation input. The sync refuses dirty
upstream catalog files. A catalog update is complete only when both JSON files, the
snapshot manifest, affected tests, and UI behavior change together. Once a released
Token Economy package exposes `ModelRoutingKnowledgeBase`, replace this sync boundary
with the package API and remove the snapshot as one migration.

## Picker and validation behavior

`GET /api/models` exposes every synchronized policy row for diagnostics. The picker
offers only `selectable` and `fallbackOnly` rows and filters them by CLI (`codex`,
`claude`). Unsupported, restricted, and deprecated rows remain visible in the API but
cannot start a run. The host also joins the policy with read-only local CLI discovery
asynchronously during startup, with bounded probes and no synchronous wait on tasks.
`codex debug models` must list each Codex model with `visibility: list` before it is
offered; this rule applies to all Codex policy rows without a separate model-id list.
Policy rows with a `minimumCliVersion` also require an installed version at least that
high. In this snapshot GPT-6.1 Sol requires Codex 0.166.0 and Claude Sonnet 5.5 requires
Claude Code 2.1.284. Missing, failed, or malformed
probes do not establish support. Probe failures, including permission and timeout errors,
supply no evidence for that probe; caller cancellation still propagates. A blocked model
stays visible and priced in diagnostics,
but `availableForNewRuns` is false and explicit selection is rejected. Restart the host
after upgrading or changing CLI access to refresh this evidence. The library's
`ReviewModelCatalog.Default` is the policy-only view; the API uses the local evidence
view for both the picker and run validation.

The September 29 ladder refresh remains in effect: GPT-6 Sol and GPT-5.6 Sol/Terra
support low through max plus ultra; GPT-6 Luna and GPT-5.6 Luna support low through max.
These models reject minimal. v0.3.7 advertised minimal through xhigh for GPT-6.1 Sol;
the retained v0.3.9 policy supersedes that observation with low through max.

A compatible unknown id remains accepted (`gpt-*` for Codex, `claude-*` for Claude,
`gemini-*` for Gemini/Antigravity) so a newly released model is not blocked before the
next catalog sync. Such an id has no asserted capability or price. Known non-routable
models are never reclassified as custom ids.

## Usage pricing

`ReviewPriceCatalog.Default` contains GPT-6.1 Sol from 2026-09-29 and Claude Sonnet
5.5 from 2026-09-28, independently of local CLI availability. Both currently have
short-context rates of $2 input, $0.10 cache read, $2.50 cache write, and $10 output
per million tokens. Sonnet's $0.10 cache-read rate includes the later upstream
correction to the v0.3.7 snapshot's $0.20 transcription.

Ledger queries resolve each entry against its operation date using the synchronized
prices, replacing missing or stale recorded estimates without rewriting ledger history.
`GET /api/usage` exposes the resulting `estimatedCost` and aggregate `costStatus`
(`priced`, `partial`, or `unpriced`); individual successful estimates retain the
existing resolver status `resolved`. For example, 80k total input including 20k cache
reads and 10k cache writes, plus 10k output, costs $0.227 for either model at launch.
Usage that cannot establish a prompt-size tier retains the existing ambiguous-price
behavior rather than claiming a short-context estimate.

The first picker choice is always the policy default. Choosing it sends no model or
thinking override; the API then resolves the routing policy's route for the CLI
(`ReviewModelCatalog.ResolveDefault`) and passes that model and thinking level to the
CLI explicitly, so the run manifest, the run response, the sidecar, and the usage ledger
name the model that actually served the run, tagged `modelSource: policy-default`. For
Codex this is the recommendation itself; for Claude it is the policy's provider fallback
for the recommended route, or the strongest fallback-eligible Claude model when the policy
withholds every fallback from that route (the recommendation still names the floor it
does not reach). A CLI without policy rows keeps the runner default and is tagged
`modelSource: runner-default`. The below-floor confirmation applies to explicit routes
only. A selected thinking level is validated against the model policy and passed to
CodingAgentRunner's first-class `ThinkingLevel` request field.

## Correctness-floor ladder

`IsBelowCorrectnessFloor` compares an explicit route against the recommended hard floor
using a ladder read from the synchronized policy, not from a model list in C#:

- every `routes` entry whose `workflowRole` is `coreTask` contributes its `modelId`,
  `thinkingLevel`, and `rank`; a selection qualifies at that rank when it names the same
  model at that thinking level or stronger;
- every `providerFallbacks` entry qualifies its `modelId` at the strongest rank among the
  `forRouteIds` it is declared for, after removing `notForRouteIds`; and
- a model the policy qualifies for no core-task route ranks below every floor above the
  lightest one.

`forRouteIds` is an allow-list, and that is what keeps an equivalent-provider fallback
honest: `claude-sonnet-5` is declared only for `terra-medium` and `sol-medium`, so it
satisfies the broad-contract floor and never the correctness-critical one, at any thinking
level. `notForRouteIds` is redundant against the current policy — the two lists do not
overlap — and is applied only so a future policy that contradicts itself resolves in the
restrictive direction. Price and quota still contribute no downward adjustment.

Route ids are the join between this ladder and `Recommend`, which still names its score
bands and floors in code. That join is checked when the catalog loads: a policy missing any
route the recommender can name is a load-time failure rather than a floor that silently
ranks zero, and an unrecognized floor id reports below-floor rather than skipping the gate.
Adding a model to the ladder stays a catalog sync; adding a *route* still needs a code
change here.

## Evidence artifact

Every durable review directory contains `result.json` beside `manifest.json`,
`progress.jsonl`, and `status.json`. It is atomically refreshed with run state and has
top-level `model`, `thinkingLevel`, and `cli` fields for Token Economy evidence import.
Missing overrides are explicit as `runner-default` and `model-default`; they are not
silently inferred. The artifact also includes scope, state, timestamps, counts, usage,
cost status, and stop reason.
