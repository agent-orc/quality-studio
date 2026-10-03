# Model catalog integration

Quality Studio treats Token Economy's model-routing policy and price catalog as the
authority for review model identity, capability, thinking levels, and routing status.
The review UI does not maintain a second hand-written model list.

## Integration decision

Quality Studio synchronizes the TokenEconomy `0.3.5` release through the existing
snapshot boundary. A future migration to the .NET package API is separate work;
this release keeps pricing and routing provenance reviewable together:

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
snapshot manifest, affected tests, and UI behavior change together. A future package
migration must replace this sync boundary and remove the snapshot in one change.

## September 2026 model and CLI contract

The synchronized release makes these canonical IDs selectable for explicit reviews.
Prices are standard API estimates in USD per million tokens, effective from
2026-09-22 UTC; earlier operations return `noPriceForDate`.

| Model | Canonical ID | Review CLI | Minimum installed CLI | Input / cached input / output |
| --- | --- | --- | --- | --- |
| Claude Opus 5.5 | `claude-opus-5-5` | `claude` | Claude Code `2.1.281` | 4 / 0.20 / 20 |
| GPT-6 Sol | `gpt-6-sol` | `codex` | Codex CLI `0.155.0` | 2 / 0.20 / 10 |
| GPT-6 Luna | `gpt-6-luna` | `codex` | Codex CLI `0.155.0` | 0.10 / 0.01 / 0.50 |

The CLI minimums are operational requirements carried in catalog notes, not an
installed-version check performed by the picker. Upgrade the runner host before
selecting these models. Operator probes on 2026-09-24 confirmed Sol execution on
Codex CLI 0.155.0 and Opus 5.5 execution on Claude Code 2.1.281. Luna was discovered
by Codex CLI 0.155.0, but successful execution remains **unverified**. Codex CLI
0.154.0 rejected the new OpenAI IDs on ChatGPT accounts; Claude Code 2.1.270 silently
fell back to Haiku after an unrecognized-model warning for Opus 5.5.

`GET /api/models` exposes these rows with `routingStatus: selectable`,
`availableForNewRuns: true`, and `priceAvailable: true`. The picker filters Opus to
Claude reviews and Sol/Luna to Codex reviews. Selectability and price availability
do not establish provider/account availability. Explicit selections retain
`modelSource: explicit`; adding these entries does not change the policy default
or relax its correctness floors. GPT-6 Astra remains priced at 10 / 1 / 50 from
2026-09-03.

These are standard-tariff estimates; this integration does not select batch, flex,
fast-mode, or long-context tariffs from usage telemetry. Upstream price provenance
is recorded in the synchronized price JSON, including the Anthropic release notes
and pricing page and the OpenAI changelog and pricing page. Historical repricing is
defined in [the usage contract](usage-telemetry.md#query-time-repricing).

## Picker and validation behavior

`GET /api/models` exposes every synchronized policy row for diagnostics. The picker
offers only `selectable` and `fallbackOnly` rows and filters them by CLI (`codex`,
`claude`). Unsupported, restricted, and deprecated rows remain visible in the API but
cannot start a run. The current Token Economy policy has no Google provider rows, so
Gemini and Antigravity show Runner default plus the explicit free-text escape hatch;
Quality Studio does not invent a capability tier in that gap.

A compatible unknown id remains accepted (`gpt-*` for Codex, `claude-*` for Claude,
`gemini-*` for Gemini/Antigravity) so a newly released model is not blocked before the
next catalog sync. Such an id has no asserted capability or price. Known non-routable
models are never reclassified as custom ids.

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
