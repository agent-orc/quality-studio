# QS-112 status

Result: Done

## Integration recovery

- Continued from the latest reviewed delivery `9660e52911bc18e9883dd5978e799e2cafc39b9b` on the task branch, preserving the earlier delivery commits. That delivery received `ProductFailure` at review `review_ad52189298564a5bab917782f432e3b9`.
- Fetched current `origin/main` at `c0b3739ba016d4e623eac05986a23bb6701f4932`. It was already merged into the task branch by `1164cee174b0c6810f745f1aea60abdc0ff67a5c`; `git merge origin/main` reported “Already up to date.” No conflict remained. The earlier failed stage was `merge-into-develop` into `main`; this delivery remains on the task branch and does not push `main`.
- Retained the delivered option: one `rule-set.v1` file for import/export, project and global writable scopes, and project applicability selecting packs in place of the house-style default.

## Review findings addressed

- **code-quality — “Global rule writes can invalidate other repositories.”** Global `Mutate` and `Import` now compare diagnostics before and after the candidate write for every available registered repository (including archived registrations) while holding the global write lock. A new diagnostic rejects the write before any file or audit entry is written. `Global_custom_rule_write_cannot_invalidate_another_registered_repository`, `Global_import_preview_and_apply_reject_a_collision_in_another_registered_repository`, and `A_global_rule_cannot_collide_with_a_registered_repository_rule` prove the store and API paths.
- **tests-and-evidence — “The rule-pool UI has no shipped screenshot evidence.”** A live API and Angular UI against an isolated sample repository produced nine PNG screenshots, `rule-pool-evidence.json`, the exported rule set, and `rule-pool-evidence.log` in the collected task results directory. The screenshots show pack selection, overrides, custom rule validation and save, import preview, audit history, and a broken configuration in dark mode. The nine PNGs and evidence manifest are also committed under `results/` for direct review.
- **requirement-fit — “The required review-findings resolution mapping is missing from the redelivery status report.”** This section maps each open finding to its change and evidence. `deliverables.md` now identifies the latest ProductFailure and this fix round instead of claiming that review passed.
- **documentation-impact — pass.** The rule-pool guide remains indexed in `docs/start/README.md` and now states that global writes validate all available registered repositories.

## Verification

| Command | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed; 0 warnings, 0 errors. |
| `dotnet test QualityStudio.slnx --filter "Category!=MachineBound&Category!=ExternalLive"` | Passed; 255 API and 578 CodeQuality tests, 9 platform skips, 0 failures. |
| `npm --prefix frontend run build -- --configuration development` | Passed. |
| `npm --prefix frontend test` | Passed; 230 browser tests, 0 failures. |

The two new store tests were first run against the old validation path and failed because no exception was thrown; they passed after the fix. An initial full run had one transient, unrelated cleanup failure in `CodingAgentReviewAgentLargePromptTests.A_100_KB_prompt_reaches_the_cli_intact_over_stdin(codex)`; the test file is identical to `origin/main`, and the exact full command passed on the final run. The first-run and final logs, plus UI evidence, are in `/home/agent/runner-work/tasks/QS-112/results/`.
