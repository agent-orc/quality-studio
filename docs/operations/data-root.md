# Quality Studio data-root operations

Quality Studio treats an analysed Git checkout as read-only. Normal scans and review
runs must not create, update, or delete files below the repository root. The only
exception is an explicit user-triggered export whose destination the user selected.

## Resolution contract

`QualityStudio:DataRoot` configures the projects directory. The environment variable
`QUALITY_STUDIO_DATA_ROOT` is used when that setting is absent. The default is:

```text
%LOCALAPPDATA%/QualityStudio/projects/<repository-identity>/
```

The platform local-application-data directory is used on Linux and macOS. Repository
identity is a stable slug plus a hash of the origin URL; repositories without an origin
use their canonical absolute path. This prevents two repositories with the same folder
name, and parallel test checkouts, from sharing state.

Inside that project directory, `.quality/` retains the established logical layout:
`reviews/`, `findings/`, `reports/`, `runs/`, `usage/`, `coverage/`, and configuration
such as `inputs/` and `scope.json`. Stored subject paths remain repository-relative;
absolute checkout paths are not persisted in review metadata.
The repository registry is stored as `repositories.json` directly in the configured
projects directory; it is migrated from the API content root on first startup.

## One-time migration

When a repository is first opened after this change, startup searches the checkout for
root and per-folder `.quality` directories. Files are moved to the resolved project data
root without overwriting different data. Root data keeps its layout. Per-folder data is
placed under `.quality/by-path/<repository-relative-folder>/`, and metadata discovery
continues to index it. A `.in-tree-quality-migration-v1.json` marker makes migration
idempotent.

If a destination already contains a different file at the same path, startup fails
without deleting either copy. Reconcile the two files and restart. Back up a project by
copying its resolved project data directory while no review is running.

## Git cleanliness and exports

The repository `.gitignore` ignores `.quality/` at every depth. No runtime ledger,
sidecar, finding state, report snapshot, run state, or Quality Studio configuration is
versioned by default. To version a report, use the report export action or API and choose
an ordinary repository path such as `docs/reports/quality-report.md`; that write is an
explicit export, not runtime persistence.

At startup, Quality Studio asks Git for dirty paths and logs a warning when any root or
nested `.quality` path is dirty. Remove obsolete generated files from the index once
after upgrading. A full run should then leave `git status --short` unchanged.
