# Quality Studio project data root

Quality Studio treats every analysed Git checkout as read-only. Runtime findings,
review sidecars, usage ledgers, sensor snapshots, resumable run state, and canonical
run reports are read from and written to a per-project data root. The default base is
`%LOCALAPPDATA%/QualityStudio/projects` on Windows (the platform local-application-data
equivalent elsewhere). The final directory is `<repository-id>-<identity-hash>`;
the hash is derived from the normalized checkout identity so two installations using
the same friendly id cannot accidentally share data.

Set `QualityStudio:DataRoot` (or `QualityStudio__DataRoot`) to change the base. It
must be outside every analysed checkout. Inside a project directory, Quality Studio
mirrors legacy repository-relative paths: for example `.quality/usage/2026-09.jsonl`
and `src/.quality/reviews/files/...`. Moving a checkout or changing its registered id
selects a different project data directory; relocate the old directory as described below.

## One-time migration

At startup, the API scans a registered checkout for root and nested `.quality`
directories. Untracked files are moved to the project data root. Git-tracked legacy
files are copied and left in place so startup cannot dirty the checkout with staged
deletions. An `.in-tree-quality-migration-v1.json` marker makes the operation idempotent. Conflicting
destination files are preserved and reported rather than overwritten.

The startup check runs before migration and logs `DirtyQualityTree` as a warning when
`git status -- .quality '**/.quality/**'` reports dirty tracked data (or legacy
untracked data not yet covered by an ignore rule). This is
an operational warning; it does not mutate task or Git state.

## Versioning and exports

No `.quality` runtime artifact is meant to be committed. The repository `.gitignore`
therefore ignores `.quality/` at every depth. Reports become versioned only when an
operator explicitly exports JSON, Markdown, HTML, or SARIF to a chosen path and then
commits that exported artifact through the normal repository workflow. Quality Studio
never commits, pushes, or cleans a checkout.

Back up the configured data-root base if review history and usage accounting must
survive workstation replacement. To relocate it, stop the API, copy the complete
per-project directory, update `QualityStudio:DataRoot`, and restart.
