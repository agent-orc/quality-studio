# Model catalog integration

Quality Studio treats Token Economy's model-routing policy and price catalog as the
authority for review model identity, capability, thinking levels, and routing status.
The review UI does not maintain a second hand-written model list.

## Integration decision

The preferred long-term integration is a `TokenEconomy` .NET package reference. The
project is packable, but the currently published `0.2.0` package predates
`ModelRoutingKnowledgeBase` and does not contain the required routing-policy API or
embedded `model-routing-policy.json`. Consuming that package would therefore expose
pricing but not the required capability and retirement facts.

Until a package containing the routing knowledge base is released, Quality Studio uses
the defined snapshot path:

- `token-economy-model-routing-policy.json` and `token-economy-model-prices.json` under
  `src/AgentOrchestrator.CodeQuality/catalogues/` are exact Token Economy files;
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
cannot start a run. The current Token Economy policy has no Google provider rows, so
Gemini and Antigravity show Runner default plus the explicit free-text escape hatch;
Quality Studio does not invent a capability tier in that gap.

A compatible unknown id remains accepted (`gpt-*` for Codex, `claude-*` for Claude,
`gemini-*` for Gemini/Antigravity) so a newly released model is not blocked before the
next catalog sync. Such an id has no asserted capability or price. Known non-routable
models are never reclassified as custom ids.

The first picker choice is always Runner default. Choosing it sends no model or thinking
override, preserving prior behavior. A selected thinking level is validated against the
model policy and passed to CodingAgentRunner's first-class `ThinkingLevel` request field.

## Evidence artifact

Every durable review directory contains `result.json` beside `manifest.json`,
`progress.jsonl`, and `status.json`. It is atomically refreshed with run state and has
top-level `model`, `thinkingLevel`, and `cli` fields for Token Economy evidence import.
Missing overrides are explicit as `runner-default` and `model-default`; they are not
silently inferred. The artifact also includes scope, state, timestamps, counts, usage,
cost status, and stop reason.
