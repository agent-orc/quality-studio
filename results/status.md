# QS-117 status

Result: Done

## Decision and scope

Implemented the shadow-only verdict contract under QS-W6: Quality Studio supplies a policy-bound, rule-cited verdict for a Git change; Agent Studio remains the decision owner. This prepares the QS-118 Dossier's recommended option B without changing any gate. The reviewed `3f0130b3` delivery and subsequent QS-117 commits remain on the task branch with their history intact. Merge commit `59502d45` brought in the refreshed `origin/main` at `a0a5d660` without conflicts; both the prior delivery `032a9a14` and current main tip are ancestors.

## Review findings addressed

- **Ten new API test failures:** Removed the duplicate `/api/rules` registrations and obsolete handler; `MapRulePoolEndpoints` now owns both rule-pool routes. Added the required unscoped `POST /api/change-review` twin. The ten specifically reported tests pass. Added positive default-route and scoped-identity regression tests; a foreign repository and an unscoped default route reject identities without access.
- **Security findings bypass configured blocking policy:** Removed the security-category exception from disposition calculation and the instruction excluding security findings from the reviewer prompt. An applicable security-category code rule now blocks when `.quality/policy.json` names its rule and severity. A focused test also verifies the same finding becomes `concerns` when that rule is not selected to block.
- **Missing 20-task comparison and evidence:** Fetched Agent Studio `develop`, verified all 20 result SHAs appear in remote result refs and that each base is an ancestor of its result, called the change-review endpoint for each selected pair, retained the 20 responses, ran `scripts/compare-shadow-verdicts.mjs`, and wrote [the shadow report](shadow-comparison.md) with a per-record table and all disagreements. Exact agreement is **5/20 (25.0%)**. `AGT-2950` returned `provider-unavailable` for a binary diff and was replaced by operator-provided `AGT-3014` before scoring. The final 20 all have completed verdicts.
- **Documentation and shadow authority:** Updated the route and policy guide and linked the comparison. No gate consumes the verdict; QS-W6 remains in effect until the dossier decision changes.
- **Integration recovery:** The reported `merge-into-develop` stage failed because the reviewed delivery did not integrate. [Recovery evidence](integration-recovery-evidence.txt) records the reviewed SHA, both recovery merges, and successful ancestry checks against the refreshed main. The referenced `pipeline-execution.json` was not mounted in this workspace; the reported failure is quoted there and the branch history supplies the replacement evidence.

## Verification

| Command | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed; 0 warnings, 0 errors. [Log](build.log) |
| `dotnet test QualityStudio.slnx --configuration Release --no-build --filter 'Category!=MachineBound&Category!=ExternalLive'` | Passed; 280 API tests and 830 code-quality tests, 9 platform skips. Includes the reported route, security, and verdict regressions. [Log](dotnet-tests.log) |
| `npm --prefix frontend run build -- --configuration development` | Passed. [Log](frontend-build.log) |
| `CHROME_NO_SANDBOX=1 npm --prefix frontend test` | Passed 243/243. [Log](frontend-tests.log) |
| `node scripts/compare-shadow-verdicts.mjs results/shadow-pairs.json "$JOB_RESULTS_DIR/shadow-comparison-recomputed.json"` | Passed; 5/20 exact agreement, byte identical to the committed comparison. [Report](shadow-comparison.md) |

Frontend dependencies were installed with `npm --prefix frontend ci` before the frontend checks because this worktree initially had no `node_modules`. The full .NET run used the final Release build. All logs and comparison evidence are present in the absolute job results directory.
