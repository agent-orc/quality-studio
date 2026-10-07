# QS-101 status

Result: Done

## Review findings addressed

- **New `CodexKeepsRangesLineAccurateForAMidSizedFile_WhenExplicitlyEnabled` failure:** The September failure was caused by the gate selecting an opt-in live test without `QUALITY_RUN_LIVE_REVIEW=1`; the corrected filter excludes it. A new explicit live run exposed a real residual column error (67 where the line allows at most 51), recorded in [initial failure evidence](live-range-initial-failure.txt). The prompt now provides each source line's exact `endColumn` and directs the agent to use whole-line ranges. The tightened live test requires at least one checked location and zero out-of-range positions; it [passed](live-range-test.log).
- **Pre-existing `CodexCanReviewSmallFile_WhenExplicitlyEnabled` failure:** Same old gate misselection; unchanged on current main and excluded by the corrected required filter. No unrelated test change.
- **Rebase conflict with main's finding identity contract:** Retained main's requirement that agents omit generated `id` and `fingerprint`, while keeping source-line and column instructions in the three file review templates.
- **Aggregate digest double numbering:** Real on the replayed change. Restricted prompt line numbering to whole-file reviews and added a test confirming an aggregate digest keeps its own source line anchors.

## Verification

| Check | Result |
| --- | --- |
| `dotnet build QualityStudio.slnx --configuration Release` | Passed, 0 warnings and 0 errors. [Log](dotnet-build.log) |
| Focused `ReviewPromptBuilderTests` | Passed, 12 tests. [Log](dotnet-tests-focused.log) |
| `dotnet test QualityStudio.slnx --configuration Release --no-build --filter 'Category!=MachineBound&Category!=ExternalLive'` | Passed, 817 CodeQuality and 275 API tests; 9 platform skips. [Log](dotnet-tests-required.log) |
| `npm --prefix frontend run build -- --configuration development` | Passed. [Log](frontend-build.log) |
| `CHROME_NO_SANDBOX=1 npm --prefix frontend test` | Passed, 241 tests. [Log](frontend-tests.log) |
| Explicit live mid-sized file range test | Passed, 1 real agent run with at least one finding location and zero out-of-range positions. [Log](live-range-test.log) |

The implemented option is one-based line numbers plus the exact end column for each whole-file source line. Aggregate digests already carry source anchors and are left as supplied.
