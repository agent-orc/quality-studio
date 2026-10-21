# QS-127 status

Result SHA: `0dc1e3f21ea9ab66e17d422caa8a268cad62b467` on `runner/agent-runner-01/QS-127`. This
delivery also covers QS-121.

## What changed

- `npm run catalog:sync -- --source <clean clone of agent-orc/token-economy at tag v0.3.8>` was run.
  It synced upstream `c6d1de5fcf37cc25d047811138f4e12e71b8de1a`, policy `2026-10-21`.
  `catalog:check` passes with and without `TOKEN_ECONOMY_REPOSITORY`.
- `claude-haiku-5-5`, `gemini-4-argon` and `gemini-3.8-flash` are priced. The QS-121 models
  `gpt-6.1-sol` and `claude-sonnet-5-5` are priced too, along with `claude-opus-5-5`,
  `gpt-6-sol` and `gpt-6-luna`.
- Prompt-size tiers are applied through `ReviewPriceCatalog.Price`. An operation is priced at the
  base tier only when its summed prompt is below the first tier. Otherwise the status is
  `ambiguousPromptSize`: `total` is null and `lowerBound`/`upperBound` are set. The cost cap uses
  the upper bound.
- `ReviewModelCatalog.Recommend` takes route models from the synchronized policy, because 0.3.8
  re-pointed the Luna and Sol routes to `gpt-6-luna` and `gpt-6-sol`.

## Decisions

- No dossier existed for this card. The ambiguity is shown as Token Economy's own
  `ambiguousPromptSize` status with a null total. Bounds are added so the range stays visible.
  I did not report the cheap tier, and I did not drop the cost without a range.
- nuget.org did not list `TokenEconomy` 0.3.8 at 2026-10-21 02:12 UTC. The registration index
  only showed 0.3.7. The `v0.3.8` tag was about 10 minutes old, so this is likely indexing lag.
  Quality Studio syncs from the git tag, not the package, so the sync is unaffected.

## Verification

- `dotnet build QualityStudio.slnx --no-restore`: no errors, no warnings.
- AgentOrchestrator.CodeQuality.Tests: `PromptSizeTierPricingTests`, `ReviewModelCatalogTests`,
  `UsageLedgerTests`, `ReviewUsageForecasterTests`, `FlowReviewRunnerTests`,
  `ReviewAgentUsageTests`, `RuleEffectivenessTests`, `ReviewRunnerTests` and
  `ProcessStartGuardTests` pass.
- QualityStudio.Api.Tests: `ApiSmokeTests`, `ReviewRunStoreTests`, `OperatingLimitTests`,
  `QualityRunReportFactoryTests`, `ReviewProviderFailureTests`, `ReviewRunRetentionTests` and
  `LocalModeBindingGuardTests` pass.
- Guard filter `FullyQualifiedName~.Architecture.|Category=Guard` on `QualityStudio.slnx`: no test
  in this repository matches it.
- Frontend: `usage-history.spec.ts` passes (8/8). `npm --prefix frontend run lint` is clean.
