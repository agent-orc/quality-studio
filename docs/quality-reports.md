# Quality reports

Quality Studio exports one versioned report model through both the HTTP API and
the standalone `quality` CLI. The scorecard contains the effective grade per
review kind and hierarchy level, finding counts by severity and lifecycle state,
the fresh/stale/policy-drift/missing distribution, file coverage, and configured
sensor posture. Report generation does not run or install sensor tools;
availability is explicitly `null`/not probed. Repository roots are deliberately
omitted from exported data.

The project score is the rounded mean of kinds that have review evidence. A
repository with no scored kind has score `0`. A kind score is the rounded mean
of its current review sidecars; level rows expose the same calculation within
each hierarchy level. Coverage counts a source file once when it has any review,
regardless of kind. Staleness counts file-kind pairs, which makes missing review
coverage visible instead of treating absence as a passing grade.

Finding state comes from `.quality/findings/state.json`. Open and accepted
findings remain active and affect `--fail-on`; waived, false-positive, and
resolved findings do not fail that gate. All states remain represented in JSON
counts. A lifecycle record whose observation is no longer in a current sidecar
is reported with `unknown` severity rather than inventing one. SARIF omits
resolved observations and represents waived and false-positive results with
accepted external suppressions.

## CLI

```shell
quality report . --format markdown
quality report . --format html --output quality-report.html
quality report . --format json --output quality-report.json
quality report . --format sarif --output quality-report.sarif
quality report . --fail-under 80 --fail-on high
quality report . --run <run-id> --format markdown --output quality-run.md
quality report . --run <run-id> --format sarif --fail-on high --output quality-run.sarif
```

Supported formats are `markdown`, `html`, `json`, and `sarif`. Without
`--output`, the document is written to standard output. Gates are evaluated
after the report is generated, so a failing pipeline can still publish the
artifact.

Exit codes are stable:

| Code | Meaning |
| ---: | --- |
| `0` | Report generated and every requested gate passed. |
| `1` | Report generated, but `--fail-under` or `--fail-on` failed. |
| `2` | Invalid arguments or report generation failed. |

`--fail-under` accepts an inclusive score from 0 through 100.
`--fail-on critical|high|medium|low|info` fails when an active finding exists at
that severity or higher.

## Run-scoped reports

Every terminal UI review run writes a strict canonical document to
`.quality/reports/runs/<runId>.json` and a self-contained presentation artifact to
`.quality/reports/runs/<runId>.html`. The snapshot contains its immutable subject
manifest, routing provenance, usage and cap outcome, one explicit outcome per
planned unit, the exact sidecar bytes captured by the run, finding lifecycle
state, and a comparable-fingerprint delta. `done`, `failed`, `cancelled`, and
`capped` runs are all reportable. Incomplete outcomes are visibly marked
`partial` and do not invent a score or baseline state.

A fresh skip is represented as `skipped-fresh` with `producedByRun: false`; it is
counted as reused evidence, not as a model operation. A capped run writes its
current revision before stopping. Resuming the same run keeps its ID and creates
the next canonical revision after the later terminal transition. Atomic
same-directory replacement ensures readers see either the previous complete
document or the next one, never an incomplete temporary write.

HTML, bounded Markdown, JSON, and run-scoped SARIF are projections of this one
document. Terminal publication writes the HTML projection beside the JSON, and
recovery backfills or refreshes it from canonical truth when necessary. The HTML
is self-contained, uses a restrictive content security policy, identifies the
repository and HEAD captured when the run was enqueued, and presents review
verdicts, findings, the complete token ledger, and provenance without external
assets.
Markdown includes at most 20 active findings and states the omitted count. SARIF
uses relative paths, stable automation and fingerprint identities, and only emits
baseline state when the run has a comprehensive comparable predecessor. Exported
documents omit the absolute repository root.

## HTTP

`GET /api/report` builds a comparison report for every active registry
repository the caller can access. `GET /api/repos/{repoId}/report` limits it to
one repository. JSON is the default; append `?format=markdown`, `html`, `json`,
or `sarif` to select an export representation. The response media types are
`text/markdown`, `text/html`, `application/json`, and
`application/sarif+json`.

For one review run, use
`GET /api/review/runs/{id}/report?format=...` or
`GET /api/repos/{repoId}/review/runs/{id}/report?format=...`. HTML is served inline
from the saved artifact so the UI can open it directly; Markdown, JSON, and SARIF
retain attachment filenames appropriate to their formats. Repository access is
resolved through the same registration boundary as the existing run routes.

`GET /api/review/runs/trend?kind=code&scopeUnitId=<id>&level=file` and its
repository-scoped form return paged run history. A series is keyed by repository,
kind, scope unit ID, and level. Only complete runs are comparable; partial runs
remain visible as events, and the highest revision wins for a resumed run. This
run trend is separate from the Git-backed commit trend below.

The JSON contracts are described by
[`schemas/quality-report.v1.schema.json`](../schemas/quality-report.v1.schema.json)
and
[`schemas/quality-run-report.v1.schema.json`](../schemas/quality-run-report.v1.schema.json).
SARIF declares version 2.1.0 and the official OASIS schema URI, produces one run
per repository, preserves stable finding fingerprints, and includes scorecard
and trend data in run properties.

## Git-backed commit trend

Trend storage is Git itself. Quality Studio finds commits that changed review
sidecars, reconstructs the complete sidecar set at each such commit, and emits a
score point only when the per-kind curve changes. Commit IDs and author
timestamps identify every point. No report database or new history file is
written.

The committed sample generated for this repository is
[`results/quality-report.sample.md`](../results/quality-report.sample.md).
