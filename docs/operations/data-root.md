# Quality Studio project data root

Quality Studio does not write runtime state into an analyzed checkout. Source files
and Git metadata are read from the registered repository root. Findings, review
metadata, ledgers, run state, sensor snapshots, and project review configuration are
read and written below a separate project data directory.

## Resolution contract

`QualityStudio:DataRoot` configures the base directory. The environment-variable
form is `QualityStudio__DataRoot`. A relative value is resolved from the API content
root. When omitted, the base is the operating system local-application-data folder
plus `QualityStudio/projects`, which is
`%LOCALAPPDATA%\QualityStudio\projects` on Windows.

Each immutable repository registration ID selects one child directory:

```text
<data-root>/<project-id>/
  findings/state.json
  inputs/*.md
  reviews/files/*.review-meta.*.json
  reports/runs/*.json
  runs/<run-id>/...
  usage/YYYY-MM.jsonl
```

The configured base must be outside every analyzed checkout. Startup rejects a
configuration that would place the project data directory inside its checkout.

## One-time migration

At repository registration or startup, Quality Studio checks for root and nested
`.quality` directories. Before changing them it asks Git for dirty `.quality` paths
and emits a `DirtyQualityTree` warning when any exist. It then moves root data to the
new project directory and consolidates nested legacy review sidecars under
`reviews/<level>/`. Conflicting destination content stops migration
instead of overwriting either copy. `.migration-v1.json` records completion and the
number of moved files.

Back up the checkout and data root before manually retrying a failed migration. To
retry after resolving a conflict, remove only the project directory's migration
marker. Do not point two unrelated repositories at the same registration ID and
data-root base.

## Source control and exports

No runtime data-root artifact is versioned by default. In-tree `.quality/` folders
are ignored as a safety net. Use `GET /api/report?format=json|markdown|html|sarif`
or the `quality report --output <path>` command for an explicit export. The operator
chooses the export path and may commit that exported snapshot when it is a deliberate
project artifact.

After migration, run `git status --short` in the analyzed checkout. Once historical
tracked `.quality` files have been removed in a normal source change, a complete
Studio review must leave that status unchanged.
