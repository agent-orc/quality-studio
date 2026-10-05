# QS-112 status

Result: Done

## Round

- Targeted fix round for review `review_f8c56165854845c0836350246f9e93da`. Only its open blocking finding is fixed.
- Started from the latest delivery `3259ff2c` (`refs/remotes/origin/agent-studio/results/run_df3feadfb90e413582bffc7d0ef9cacd/fence-22/…`). All reviewed commits are kept, with no squash, split or drop.
- Merged `origin/main` once. It was already contained at `bfe24ce5` ("Already up to date"), so no merge commit was needed.
- Fix commit: `9bab533f fix(rule-pool): refuse writes through linked rule folders`.
- No push to `main`.

## Findings addressed

- **code-quality (block): "Rule-pool writes can follow linked directories outside their scope."**
  - Root cause: `RuleScopeSources.Read` reports a linked folder as a diagnostic. `Mutate` and `Import` reject only *new* diagnostics, so a write that added none still reached `RulePoolStore.Write`, which then wrote through the link.
  - Fix: `RulePoolStore.Write` now checks the scope rule folder, `custom/` and `packs/` for a symbolic link before it touches any file. If one is linked, it throws `RulePoolValidationException` with a located diagnostic, and the API answers 400. No file is written and no audit entry is appended (the audit is written after `Write`).
  - File links were already safe and are unchanged. `AtomicFile` renames over a link rather than following it, and a delete removes the link, not its target. The existing test `Replace_import_repairs_linked_or_oversized_overrides` covers this.
  - Regression test: `RulePoolTests.Writes_never_follow_a_linked_rule_folder` (4 cases):
    - a linked scope folder with a custom-rule write;
    - a linked `custom/` with a custom-rule write;
    - a linked `packs/` with a pack write;
    - a linked `custom/` with a merge import.

    Each case asserts the rejection, an empty link target and an empty audit. Before the fix, all 4 failed with "No exception was thrown". All 4 pass after it.
  - Docs: one sentence was added to `docs/rule-pool-management.md`, beside the existing linked-folder paragraph.

## Verification

| Command | Result |
| --- | --- |
| `dotnet test backend/tests/AgentOrchestrator.CodeQuality.Tests --filter "FullyQualifiedName~RulePoolTests.Writes_never_follow"` before the fix | Failed 4 of 4, which is expected and proves the defect. |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed: 0 warnings, 0 errors. |
| `dotnet test QualityStudio.slnx --configuration Release --no-build --filter "FullyQualifiedName~RulePool"` | Passed: 54 of 54 `RulePoolTests` and 2 of 2 `RulePoolGlobalScopeTests`. |

The full suites were not run in this round, as the round's rules direct, because the review gate runs them. No frontend file changed, so the frontend tests were not run. The previous round's full-gate result was 650 CodeQuality, 273 API and 236 frontend tests passing. The UI evidence (nine screenshots, the evidence manifest, the log and the exported rule set) is unchanged from earlier rounds.
