# QS-129 status

Result: Done. Verified on `agent-runner-01` in fresh clones of implementation commit `da61bbba37f1f7c622178207f59b0526410f0464`.

## Decision and change

No Dossier for QS-129 was present in this checkout. Implemented the project-command option: `.agent-studio/prepare` is the sole installer (locked .NET restore, `npm ci`, Chromium) and installs Chromium in an absolute workspace path. The configured frontend test command sets `PLAYWRIGHT_BROWSERS_PATH` to that same absolute path before running `npm --prefix frontend run test`. `CHROME_BIN` remains the browser resolver's first choice. The configured lint command calls `.agent-studio/prepare` if its isolated workspace has no ESLint binary; direct `npm --prefix frontend run lint` emits a BuildProfile preparation message when dependencies are absent.

## Fresh-clone review sequence

The first clone contained no `frontend/node_modules`. The runner's injected `PLAYWRIGHT_BROWSERS_PATH` was unset for the sequence to reproduce the review shell. Commands below were run from the checkout root in the order listed, exactly as `.agent-studio/project.yml` specifies. Logs are in the QS-129 job results directory.

| Command | Result | Log |
| --- | --- | --- |
| `.agent-studio/prepare` | Pass; .NET restore, 680 npm packages, workspace Chromium installed | `fresh-prepare.log` |
| `dotnet build QualityStudio.slnx --no-restore` | Pass; 0 warnings, 0 errors | `fresh-dotnet-build.log` |
| `npm --prefix frontend run build` | Pass; existing Angular warnings | `fresh-frontend-build.log` |
| `dotnet test QualityStudio.slnx --filter "Category!=MachineBound&Category!=ExternalLive"` | Pass; CodeQuality 834 passed, 9 skipped; API 280 passed | `fresh-dotnet-test.log` |
| `.agent-studio/test-frontend` | Pass; Chrome Headless, 243/243 specs | `fresh-frontend-test.log` |
| `.agent-studio/lint` | Pass; `prelint` check and ESLint | `fresh-lint.log` |

The explicit request to prove all review commands pass in a fresh clone governed the full test commands above. No coverage run was made.

## Focused and isolated checks

- `npm --prefix frontend run test:browser-resolver`: 4/4 passed, including explicit `CHROME_BIN` precedence (`browser-resolver.log`).
- `dotnet test backend/tests/AgentOrchestrator.CodeQuality.Tests/AgentOrchestrator.CodeQuality.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~.Architecture.|Category=Guard"`: exit 0; no matching tests (`guard-codequality.log`).
- `dotnet test backend/tests/QualityStudio.Api.Tests/QualityStudio.Api.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~.Architecture.|Category=Guard"`: exit 0; no matching tests (`guard-api.log`).
- In a second clean clone with no `node_modules`, `.agent-studio/lint` ran `.agent-studio/prepare` and ESLint successfully (`isolated-lint.log`).
- Before preparation, direct `npm --prefix frontend run lint` exited 1 with `BuildProfile preparation missing: frontend/node_modules/.bin/eslint was not found`, rather than exit 127 (`unprepared-lint.log`).
- The complete .NET test logs contain no failed tests. No test process from either QS-129 clone remained at completion. All evidence files are below 8 MB.

## Re-review impact

QS-122, QS-123, QS-127, and QS-128 are eligible for re-review after this infrastructure fix. The supplied QS-127 settlement confirms that its base and candidate passed prepare and build, while both failed only the frontend browser and lint aspects; **QS-127 is the card confirmed blocked only by this ReviewInfra defect**. The source reports the same ReviewInfra escalation for QS-122, QS-123, and QS-128, but their individual review settlements were not supplied here, so an exclusive blocker cannot be asserted for those three.

The final delivery SHA is recorded in the shipped job-results copy of this file.
