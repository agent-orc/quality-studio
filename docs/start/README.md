# Quality Studio contract documentation

Start with the [product README](../../README.md) for setup and review commands.
The following documents define the model-selection and usage contracts:

- [Model catalog integration](../model-catalog-integration.md) owns snapshot
  provenance, picker behavior, thinking levels, and correctness floors.
- [September 2026 model and CLI contract](../model-catalog-integration.md#september-2026-model-and-cli-contract)
  documents `claude-opus-5-5` (Claude Code 2.1.281 minimum), `gpt-6-sol` and
  `gpt-6-luna` (Codex CLI 0.155.0 minimum). Luna execution remains unverified.
- [Usage telemetry](../usage-telemetry.md#query-time-repricing) defines ledger-v3
  read-time repricing of `cost.status: unknownModel`, the successful wire status
  `resolved`, API aggregate behavior, and the append-only storage guarantee.
- [API reference](../api.md) and [review runs](../review-runs.md) document HTTP
  requests, durable run state, and execution behavior.
