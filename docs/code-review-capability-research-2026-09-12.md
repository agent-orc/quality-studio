# Code-review capability research and finding exchange

Research and API integration snapshot: 12 September 2026.

Code review needs its own evidence: which known defects were detected, how often
emitted findings were correct, and what it costs to inspect them. General coding
scores do not establish those properties. Token Economy now has a dedicated
`BenchmarkCapabilityClass.CodeReview` category in its development API, with 24
sourced measurements in five distinct studies. Its [review guide](https://agent-orchestrator.dev/token-economy/code-review/)
and [cited research](https://github.com/agent-orc/token-economy/blob/main/docs/code-review-research-2026-09-12.md)
keep source metrics and configurations separate.

## Research findings for Quality Studio

- **Astra is a coverage candidate in the September CodeRabbit study.** It finds
  61.3% of labeled bugs, versus Sol's 59.0% and Opus 5's 50.2%. The source does
  not disclose precision, effort or sample size, so it does not identify the
  reviewer with the fewest false alarms. [Primary study](https://www.coderabbit.ai/blog/gpt-6-astra-code-review-evaluation).
- **Opus 5 has a measured effort tradeoff.** Under the same senior-reviewer
  profile, xHigh has 39.3% actionable precision and 55.2% known-issue coverage;
  high has 35.6% and 55.6%. These are three-run averages without confidence
  intervals. Full-stream precision is a different metric. [Primary study](https://www.coderabbit.ai/blog/opus-5-model-review).
- **Fable 5.1's internal Low setting beats High in that particular pipeline.**
  Recall/precision are 61.0%/37.3% versus 57.1%/36.4% on 45 tasks with 105 known
  issues. Low/High do not establish provider reasoning settings. Preserve
  `Unspecified` in the benchmark API until that mapping is sourced.
  [Primary study](https://www.coderabbit.ai/blog/fable-5-1-model-review).
- **Count findings and known issues separately.** Kodus's pinned scorecards
  expose both populations; several findings can match one issue. Partial goldens,
  the matching judge and missing tool replies limit what a replay proves about
  live review. [Methodology](https://www.codereviewbench.com/) and
  [pinned scorecards](https://github.com/kodustech/codereviewbench/tree/531297bf50e5f065e3888d7e07b55dcacbf8df64/scorecards).

These studies do not form a cross-study ranking. Record precision with assessment
coverage; measure recall against a declared issue set; include clean reviews and
false findings per reviewed PR. Do not turn suppression, accepted risk or an
unmatched plausible observation into a verified false claim.

## Implemented API exchange

The existing read endpoint remains:

```text
GET /api/repos/{repoId}/review/runs/{id}/report?format=json
```

The `quality-run-report.v1` JSON now retains available metadata captured with each
operation, in addition to its existing IDs, findings and usage:

| Path | Preserved native data |
| --- | --- |
| `observations[].sourceRevision` | The source revision recorded in the captured review metadata. |
| `observations[].reviewer` | `ReviewerIdentity`, including captured model and any separately recorded requested model/thinking level. |
| `observations[].findings[].anchors` | Native exact locations and captured excerpts/hashes. |
| `observations[].findings[].evidenceItems` | Typed evidence and its observed/unverified state. |
| `observations[].findings[].reproduction` | Recorded reproduction state and reason; unknown remains unknown. |

These optional properties reuse the existing review-meta types. Absent fields are
omitted, so older captured reports remain readable by the updated reader. Consumers
using an older closed schema or strict DTO must adopt the updated schema/DTO before
reading reports that contain the added fields. See [quality reports](quality-reports.md).

This preserves the existing export boundary for Code Studio or another host. It
does not introduce a second findings database or infer executed reasoning effort.
`run.model` and `run.thinkingLevel` still describe the manifest request. Keep those
separate from captured per-operation provenance and unknown execution facts.

The JSON report is a retained terminal snapshot, but its file can be replaced by
a later revision. Its revision is not an append-only assessment history. Subsequent
finding-state changes do not automatically republish it. Receivers that need an
evidence cutoff must retain the exact bytes/hash and run/revision they read.

## Token Economy and Studio responsibilities

Token Economy's native `quality-studio-review-run` schema-v1 drop is a separate
contract from this report. The existing importer aggregates actual route, scope and
optional confirmed/dismissed totals. It cannot infer these from requested routes,
grades, accepted/waived/resolved states or suppression. The committed TE report has
one excluded fixture and no eligible operational runs, so no local model review
suitability is asserted.

The [review-evidence dossier](operations/review-quality-evidence/index.html) already
defines the remaining bridge: independent assessments, immutable observations and
retained cutoffs. TE-38 v2 is still a planned exporter/receiver contract. Outcome
revisions must preserve the original run identity instead of being counted as new
runs. This change preserves richer native evidence for that work; it does not claim
that the bridge or an adjudication workflow has shipped.

The [governed model catalog](model-catalog-integration.md) is refreshed from
Token Economy commit `98ddcc91fba414919231e242de01dc022aed74dd`. Its retained manifest
records the source and hashes. Astra and Fable 5.1 are selectable with supported
effort levels and provisional evidence status; all 22 catalog models have sourced
API price history. Existing routing defaults and correctness floors remain in place.
The price-weighted usage estimate is an API-equivalent consumption metric; it is
not a subscription charge or a provider quota calculation.

Published review benchmark records remain in Token Economy's separate benchmark
catalog. A host can read the dedicated review category through the source API or a
package release that includes it, then retain its source and protocol when
presenting a candidate. The synchronized routing snapshot does not turn those
external studies into local qualification.
