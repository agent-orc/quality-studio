# QS-112 status

Result: Done

## Integration recovery

- Started from reviewed delivery `7a3032b28f0b50bd8f2e32e0be7b6a8d9ae13be9` (ProductFailure review `review_f66f577d860248618541c9aac60fdd36`). Its delivery commit history is preserved: no squash, split or drop.
- Merged current `origin/main` at `bfe24ce53fe1da1e77aa4d0e5c0ebb5b5a015f0c` (11 new commits, including QS-110 usage accounting) in merge commit `c8b21c53`. The only conflict was `CHANGELOG.md`, where both sides added an independent section. Both sections were kept. The earlier failed integration stage was `merge-into-develop` into `main` (evidence reference: `pipeline-execution.json`). No integration branch was moved or pushed.
- Fix commit: `5ab7d9fd fix(rule-pool): report null rule-set fields and list entries instead of throwing`.

## Review findings addressed

- **code-quality (block): "Invalid rule-set imports can cause an unhandled null exception."** Fixed. `RuleMarkdown.ValidateStructured` now checks every required string, every list element and every change-history entry for an explicit JSON `null`, and reports each one before any string method or `Render` runs. A dry run returns `valid: false` with located diagnostics, and an apply answers 400.
  - Tests: `RulePoolTests.A_rule_set_custom_rule_with_a_null_field_is_reported_not_thrown` covers 10 cases: `title`, `category`, `statement`, `goodExample`, `badExample` and `since` set to null, a null `kinds` entry, a null `deterministicRuleIds` entry, a null `changeHistory` entry, and a null `change`. Before the fix, the title, category, example and change-history cases threw from `ValidateStructured`. The null `statement` case and the null `deterministicRuleIds` entry were reported without naming the field or were silently dropped. The `since` and `kinds` cases were already reported, and now stay covered.
  - API test: `ApiSmokeTests.Rule_set_import_with_a_null_custom_rule_field_answers_with_diagnostics_not_a_server_error` posts the reviewer's exact case (`title: null`). The dry run answers 200 with diagnostics and the apply answers 400 with diagnostics, not 500.
- **Same class of defect, found by reviewing the rest of the change:** user-supplied `null` reaching code that assumes a value. Each instance below crashed before this fix and now produces a diagnostic.
  - A null `include[].ids` entry in a pack threw `ArgumentNullException` in the resolver's `definitions.ContainsKey`. This applied to a rule-set import, `PUT …/rules/packs/{id}` and a hand-edited pack file. `RulePackRules.Validate` now rejects null entries in every selector list and in `projectTypes`.
  - A null pack name in applicability threw from `packs.ContainsKey` in `ValidateApplicability`. This applied to an import and to a hand-edited `applicability.json`, so `GET …/rules` answered 500 instead of reporting the problem. `SetApplicability` also threw a `NullReferenceException` on `Trim`. Applicability now rejects null pack names on all three paths.
  - An imported override with a null `id` crashed `PlanChanges` (`ToDictionary` with a null key). The rule-set validation now requires an override id. A hand-edited `overrides.json` entry without an id no longer crashes a merge import plan, and the resolver still reports that entry.
  - Tests: `A_rule_set_with_a_null_entry_is_reported_not_thrown` (4 cases), `Hand_edited_files_with_null_entries_are_reported_by_inspection_not_thrown`, `Store_writes_with_null_entries_are_rejected_not_thrown`, `Merge_import_over_a_hand_edited_override_without_an_id_does_not_throw`. All failed before the fix. Across both bullets, 15 of the 17 new store test cases failed before the fix, and all 17 pass now.
- **requirement-fit: pass.** No scope change.
- **tests-and-evidence: pass.** The nine rule-pool UI screenshots, the evidence manifest, the execution log and the exported rule set are in the collected results directory, beside the four gate logs. This fix changes no UI.
- **documentation-impact: pass.** `docs/rule-pool-management.md` (import section) now states that an explicit `null` is rejected with diagnostics. No other contract changed.

## Verification (on the merged state, HEAD after the fix)

| Command | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed: 0 warnings, 0 errors (`gate-1-dotnet-build.log`). |
| `dotnet test QualityStudio.slnx --filter "Category!=MachineBound&Category!=ExternalLive"` | Passed: 650 CodeQuality tests (9 skipped on Linux) and 273 API tests, 0 failures (`gate-2-dotnet-test.log`). |
| `npm --prefix frontend run build -- --configuration development` | Passed (`gate-3-frontend-build.log`). |
| `npm --prefix frontend test` | Passed with `CHROME_NO_SANDBOX=1`: 236 of 236 browser tests (`gate-4-frontend-test.log`). Without the variable, Chromium cannot launch its sandbox on this runner host, and the run fails before any test executes (`gate-4-frontend-test-sandboxed-launch-failure.log`). This is a host limitation, not a test failure. `frontend/tests/run-tests.mjs` supports the variable. |

Gate logs and UI evidence are in `/home/agent/runner-work/tasks/QS-112/results/`.
