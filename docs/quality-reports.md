# Quality reports

Quality Studio exports two versioned report models through both the HTTP API and
the standalone `quality` CLI. `quality-report.v1` describes current repository
state. `quality-run-report.v1` is the durable account of one review sweep. The
repository scorecard contains the effective grade per
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
quality report . --run "$QUALITY_RUN_ID" --format markdown --output quality-run.md --fail-on high
```

Supported formats are `markdown`, `html`, `json`, and `sarif`. Without
`--output`, the document is written to standard output. Gates are evaluated
after the report is generated, so a failing pipeline can still publish the
artifact.

With `--run <id>`, every format is rendered from
`.quality/reports/runs/<id>.json`; the CLI never reconstructs run membership
from current sidecars. Markdown includes at most 20 active findings and states
the omitted count. HTML is self-contained, script-free, escaped, and protected
by a saved-file Content Security Policy. Run SARIF uses a stable series category,
records sweep provenance, emits both Quality Studio and
`primaryLocationLineHash` fingerprints, and includes `baselineState` only after
a complete comparable diff.

Exit codes are stable:

| Code | Meaning |
| ---: | --- |
| `0` | Report generated and every requested gate passed. |
| `1` | Report generated, but `--fail-under` or `--fail-on` failed. |
| `2` | Invalid arguments or report generation failed. |

`--fail-under` accepts an inclusive score from 0 through 100.
`--fail-on critical|high|medium|low|info` fails when an active finding exists at
that severity or higher.

## HTTP

`GET /api/report` builds a comparison report for every active registry
repository the caller can access. `GET /api/repos/{repoId}/report` limits it to
one repository. JSON is the default; append `?format=markdown`, `html`, `json`,
or `sarif` to select an export representation. The response media types are
`text/markdown`, `text/html`, `application/json`, and
`application/sarif+json`.

Run-scoped equivalents are
`GET /api/review/runs/{id}/report?format=...` and
`GET /api/repos/{repoId}/review/runs/{id}/report?format=...`. JSON is again the
default and is byte-equivalent in meaning to the stored canonical document.
The repository-scoped and unscoped routes use the same authorization boundary
as their existing run-detail routes.

The JSON contract is described by
[`schemas/quality-report.v1.schema.json`](../schemas/quality-report.v1.schema.json).
The canonical run contract is described by
[`schemas/quality-run-report.v1.schema.json`](../schemas/quality-run-report.v1.schema.json).
SARIF declares version 2.1.0 and the official OASIS schema URI, produces one run
per repository, preserves stable finding fingerprints, and includes scorecard
and trend data in run properties.

## Commit trend

Trend storage is Git itself. Quality Studio finds commits that changed review
sidecars, reconstructs the complete sidecar set at each such commit, and emits a
score point only when the per-kind curve changes. Commit IDs and author
timestamps identify every point. No report database or new history file is
written.

## Run trend

`GET /api/review/runs/{id}/trend` (and its repository-scoped equivalent) pages
canonical snapshots in the same repository, kind, scope unit, and level series.
The highest stored revision for each run ID wins. Complete `done` runs expose a
connectable score; capped, failed, cancelled, and otherwise partial snapshots
remain visible as unconnected events with their reason. Finding movement uses
stable fingerprints and reports no-baseline as `unavailable`. The default page
size is 30 and callers may request up to 100 points.

The committed sample generated for this repository is
[`results/quality-report.sample.md`](../results/quality-report.sample.md).
