# QS-127 status

Result SHA: `3bce87173b362ca0ad3ae1d76e3b5e8fce469a94` on `runner/agent-runner-01/QS-127`. This delivery also covers QS-121.

## What changed

- `npm run catalog:sync -- --source <worktree of agent-orc/token-economy at tag v0.3.8>` synced
  upstream `c6d1de5fcf37cc25d047811138f4e12e71b8de1a`, policy `2026-10-21`. nuget.org lists
  TokenEconomy 0.3.8. `catalog:check` passes with and without `TOKEN_ECONOMY_REPOSITORY`.
- `ReviewPriceCatalog` prices `claude-haiku-5-5`, `gemini-4-argon` and `gemini-3.8-flash`,
  plus the QS-121 models `gpt-6.1-sol` and `claude-sonnet-5-5`. It also prices
  `claude-opus-5-5`, `gpt-6-sol` and `gpt-6-luna`.
- Prompt-size tiers (Haiku 5.5 above 100K, GPT-6.1 Sol above 272K) are applied by
  `ReviewPriceCatalog.Price`. An operation is priced at the base tier only when its summed prompt
  is below the first tier. Above that, or when the input count is unknown, the status is
  `ambiguousPromptSize`: `total` is null and `lowerBound`/`upperBound` are set. The cost cap
  counts the upper bound. Run views show "up to ... (ambiguous prompt size)" and the usage history
  shows the range.
- `ReviewModelCatalog.Recommend` takes route models from the synchronized policy, because 0.3.8
  re-pointed the Luna and Sol routes to `gpt-6-luna` and `gpt-6-sol`.

## Decisions

- No dossier existed for this card. Implemented option: show Token Economy's `ambiguousPromptSize`
  status with a null total and both tier totals as bounds. The cheap tier is never shown as the cost.
- This run continues the salvaged fence-3 attempt (`64ad7177`). A fresh sync produced identical
  catalog files. Added here: the run-level cost label no longer shows the upper bound as an exact
  amount.

## Verification

- `dotnet build QualityStudio.slnx --no-restore`: 0 warnings, 0 errors.
- AgentOrchestrator.CodeQuality.Tests with filter `PromptSizeTierPricingTests|ReviewModelCatalogTests|UsageLedgerTests|ReviewUsageForecasterTests|FlowReviewRunnerTests`:
  58 passed.
- QualityStudio.Api.Tests with filter `ApiSmokeTests|ReviewRunStoreTests|OperatingLimitTests|QualityRunReportFactoryTests`:
  97 passed.
- Guard filter `FullyQualifiedName~.Architecture.|Category=Guard` on `QualityStudio.slnx`: no
  test in this repository matches it. `FullyQualifiedName~Guard` passes 12 tests.
- Frontend Karma, run with `--include` for format, usage-history, run-history and review-actions
  specs: 48 passed. `npm --prefix frontend run lint` is clean.
