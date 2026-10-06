# QS-112 status

Result: Done

## Integration recovery

- Recovered the reviewed QS-112 delivery `cf5be0a70b95361f5e444a13120a660497b84ddb` on `runner/agent-runner-01/QS-112`. Every reviewed commit remains in its original order.
- The earlier `merge-into-develop` stage reported "The reviewed delivery did not integrate"; its supplied failure evidence reference was `pipeline-execution.json`. The file was not mounted in this workspace. [Integration recovery evidence](integration-recovery-evidence.txt) records the new merge and ancestry checks.
- Fetched current `origin/main` at `bf7278ff97ec0ee574c2b6b5612097ff0acda6d5` and merged it into the task branch as `939ad9eb5b79d471ea6fab5b4b31b516f9b8c6c0`. The only conflict was `CHANGELOG.md`; both QS-112 and QS-116 entries were preserved. Both the reviewed delivery and current main are parents or ancestors of the merge. No integration branch was moved or pushed.

## Findings addressed

- **Rule-pool writes can follow linked directories outside their scope:** preserved the reviewed `RulePoolStore.Write` guard and `RulePoolTests.Writes_never_follow_a_linked_rule_folder` regression test from `9bab533f`. The merge did not change rule-pool implementation files.
- **Integration failure:** resolved the conflicting change history and merged current main into the delivery branch without rewriting reviewed commits. See [integration recovery evidence](integration-recovery-evidence.txt).

## Verification on merged state

| Command | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed, 0 warnings and 0 errors. [Log](build.log) |
| `dotnet test QualityStudio.slnx --configuration Release --no-build --filter '(FullyQualifiedName~RulePool|FullyQualifiedName~ReviewerIsolation)&Category!=MachineBound&Category!=ExternalLive'` | Passed, 155 tests (153 CodeQuality, 2 API). [Log](dotnet-tests-filtered.log) |
| `npm --prefix frontend run build -- --configuration development` | Passed. [Log](frontend-build.log) |
| `CHROME_NO_SANDBOX=1 npm --prefix frontend test` | Passed, 236 tests. [Log](frontend-tests.log) |

The first targeted .NET run omitted the gate's category exclusions and selected one `ExternalLive` canary, which requires separate opt-in. The rerun above excluded that category. The first frontend attempt used the default sandboxed Chrome launcher, which this host cannot start; the passing rerun used the repository's no-sandbox launcher setting. Only the affected .NET classes were run, following the targeted-round test instruction; the remote review gate runs the full .NET suite.

Rule-pool UI evidence is included as nine screenshots, [an evidence manifest](rule-pool-evidence.json), [an execution log](rule-pool-evidence.log), and [an exported rule set](rule-pool-exported-rule-set.json). The collected results are in `/home/agent/runner-work/tasks/QS-112/results`.
