# QS-112 status

Result: Done

## Integration recovery

- Continued from reviewed delivery `890a727989457541902aab34a2271c73cf888d92`, preserving its delivery commits. Remote review `review_8ee1fda29aba4a5d9e0df769f5108ccf` reported `ProductFailure` for linked rule-pool configuration.
- Fetched current `origin/main` at `c0b3739ba016d4e623eac05986a23bb6701f4932` and merged it into the task branch. It was already an ancestor; `git merge --no-edit origin/main` reported “Already up to date.” No conflicts or unrelated changes were needed. The branch retains the delivered `rule-set.v1` import/export, writable global and project scopes, and pack applicability in place of the house-style default.
- The earlier integration failure was at `merge-into-develop` into `main` (see the supplied `pipeline-execution.json` reference). This delivery stays on the task branch; `main` was not pushed.

## Review findings addressed

- **code-quality — “Linked rule-pool configuration is silently ignored, allowing disabled rules to become active.”** `RuleScopeSources.Read` now reports linked scope folders, `overrides.json`, `applicability.json`, custom-rule and pack folders, and linked files inside those folders as diagnostics. `Resolve` rejects the invalid pool. The fingerprint records links so a cached valid pool is reloaded after a file or folder becomes linked. `A_linked_override_file_fails_closed_even_after_a_valid_pool_was_cached` and the four cases of `Linked_rule_sources_fail_closed` failed before the fix and pass after it.
- **requirement-fit — pass.** This status records the reviewed SHA, integration state, finding resolution, and verification counts.
- **tests-and-evidence — pass.** Nine rule-pool UI screenshots, the evidence manifest, execution log, and exported rule set are in the collected `/home/agent/runner-work/tasks/QS-112/results/` directory and committed under `results/`.
- **documentation-impact — pass.** The indexed rule-pool guide now describes diagnostics for linked rule-pool sources.

## Verification

| Command | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed; 0 warnings, 0 errors. |
| `dotnet test QualityStudio.slnx --filter "Category!=MachineBound&Category!=ExternalLive"` | Passed; 255 API tests and 583 CodeQuality tests, 9 platform skips, 0 failures. |
| `npm --prefix frontend run build -- --configuration development` | Passed. |
| `npm --prefix frontend test` | Passed; 230 browser tests, 0 failures. |

Full command logs are in the collected results directory. The five focused linked-source cases were run before the fix and failed, then passed after it.
