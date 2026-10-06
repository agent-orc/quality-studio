# QS-112 status

Result: Done

## Integration recovery

- Recovered reviewed delivery `fdbfbbdd375003673fbd1115e30c5347542fbe82` on `runner/agent-runner-01/QS-112` without rewriting or squashing its commits.
- The failed pipeline stage was `merge-into-develop`: “The reviewed delivery did not integrate.” Its evidence reference, `pipeline-execution.json`, was not mounted here. [Recovery evidence](integration-recovery-evidence.txt) records the replacement merge and ancestry checks.
- Merged `origin/main` at `23eaa16b692d863dd267100e3a5f9bad5f9888aa` as `67e37ffde9e37db26206b0a640eaaa5e0603dac5`. Both tips are direct parents. Main was neither moved nor pushed.
- Combined QS-112 and main entries in `CHANGELOG.md`, `docs/data-root.md`, and `rules/CHANGELOG.md`. Kept main's generated `website/index.html` for its 35-rule catalogue; the catalogue check passed.

## Findings addressed

- **Rule-pool writes can follow linked directories outside their scope:** retained the reviewed `RulePoolStore.Write` guard and `RulePoolTests.Writes_never_follow_a_linked_rule_folder` regression test. The merge did not alter that implementation.
- **Integration failure:** retained all delivery commits, resolved four text conflicts, and verified that both the delivery and main are ancestors of the task tip.

## Verification on merged state

| Command | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed; 0 warnings, 0 errors. [Log](build.log) |
| `dotnet test QualityStudio.slnx --configuration Release --no-build --filter '(FullyQualifiedName~RulePool\|FullyQualifiedName~Rule_\|FullyQualifiedName~Custom_rule\|FullyQualifiedName~Applicability\|FullyQualifiedName~A_hand_broken\|FullyQualifiedName~Repository_scoped_identity\|FullyQualifiedName~AnalyzerCatalogueLink)&Category!=MachineBound&Category!=ExternalLive'` | Passed; 108 tests (87 CodeQuality, 21 API). [Log](dotnet-tests-filtered.log) |
| `npm --prefix frontend run build -- --configuration development` | Passed. [Log](frontend-build.log) |
| `CHROME_NO_SANDBOX=1 npm --prefix frontend test` | Passed; 239 tests. [Log](frontend-tests.log) |
| `node --test tests/rule-catalogue.test.mjs` | Passed; 10 tests. [Log](rule-catalogue-tests.log) |

The .NET run selected rule-pool and analyzer catalogue tests relevant to this merge. The remote review gate runs the full suite. Nine prior UI screenshots, the evidence manifest, execution log, and exported rule set are copied to `/home/agent/runner-work/tasks/QS-112/results`.
