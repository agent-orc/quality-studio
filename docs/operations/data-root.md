# Quality Studio data-root contract

Quality Studio separates the analysed repository root from its runtime data root.
Repository files are inputs. Normal scans, reviews, state changes, and report reads
must not create, edit, or delete files under the checkout.

## Resolution and layout

`QualityStudio:DataRoot` configures the base directory. The
`QUALITY_STUDIO_DATA_ROOT` environment variable is the fallback, followed by the
platform local application-data directory `QualityStudio/projects`. Each registered
repository uses a stable project id made from its registry id and a short hash of its
normalized checkout identity. The suffix prevents two checkouts registered as
`default` from sharing state.

The project directory is a shadow tree. A legacy checkout path such as
`src/Orders/.quality/reviews/files/file.<key>.review-meta.code.json` maps to
`<data-root>/<project-id>/src/Orders/.quality/reviews/files/file.<key>.review-meta.code.json`.
Root state such as `.quality/usage/2026-09.jsonl` maps the same way. This preserves
existing metadata references and permits lossless migration.

The repository registry itself remains host configuration under the API content
root's `.quality-studio/` directory; it is not analysed-project data.

## Migration

Call `POST /api/repos/<project-id>/migrate-quality-data` while no review is running.
The operation moves untracked files from every non-symlink `.quality` tree under the
checkout to its shadow-tree location. Files already tracked by Git are copied and
left untouched so migration does not dirty an integration checkout. It then writes
`.migration-v1-complete` in the project data directory. A repeated call is a no-op.
Identical destination files are accepted; conflicting files stop migration without
overwriting either version.

Migration is explicit because startup must not unexpectedly mutate a checkout. At
startup, the API runs a read-only Git status check and logs a warning when `.quality`
paths are dirty. After migration, remove any formerly tracked runtime artifacts from
source control once through the normal human-controlled Git workflow.

## Versioning and export

No runtime `.quality/**` artifact is versioned. This includes review sidecars,
findings, usage, run state, canonical reports, inventories, coverage, and project
review inputs. Repository `.gitignore` rules ignore both root and nested `.quality/`
directories.

Portable artifacts are created only through explicit exports. The report CLI/API
can render JSON, Markdown, HTML, or SARIF to an operator-selected destination. Such
an exported file may be committed when a project policy requires it; the underlying
runtime ledger is not copied back automatically.

## Operational checks

Before and after a full studio run, run `git status --porcelain` in the analysed
checkout. The outputs must match. If `.quality` paths appear, verify that the
registration reports the expected `dataRootPath`, run migration if needed, and check
for a component that was invoked outside the studio's repository context.
