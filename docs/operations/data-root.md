# Quality Studio data-root operations

## Contract

An analysed repository is a source input, not a runtime database. After the
one-time migration described below, Quality Studio does not create, update, or
delete files beneath its checkout during startup, scans, reviews, usage recording,
report retention, or finding-state changes. Explicit exports are the only later
supported checkout writes, and only to the exact path selected by the user.

`QualityStudio:DataRoot` configures the base directory. Its environment-variable
form is `QualityStudio__DataRoot`. The default is
`%LOCALAPPDATA%/QualityStudio/projects` on Windows and the platform local
application-data equivalent elsewhere. Each registry project id owns one directory:

```text
<base>/<project-id>/
  .quality/findings/state.json
  .quality/reports/runs/*.json
  .quality/runs/<run-id>/...
  .quality/usage/YYYY-MM.jsonl
  src/.quality/reviews/files/*.review-meta.code.json
```

The mirrored relative folders keep metadata associated with its source unit while
the actual checkout stays read-only. Registry state remains server-owned at
`<API content root>/.quality-studio/repositories.json` and is not project data.

## One-time migration

At startup, before review workers or snapshot warmers run, the API:

1. asks Git for dirty root or nested `.quality` paths and logs a
   `DirtyQualityTree` warning when any exist;
2. moves every checkout `.quality` file to the same relative location beneath the
   project data root; and
3. writes `.migration-v1.json` in the project data root.

If the destination already has identical bytes, the source duplicate is removed.
Different destination bytes fail migration and preserve both copies; the operator
must reconcile them before restarting. The migration marker makes later startups
read-only with respect to the checkout. Any later in-tree `.quality` folder is
ignored by Git, warned about at startup, and never used as runtime truth.

Back up the whole `<base>/<project-id>` directory to preserve local history. Restore
it to the same project-id directory while Quality Studio is stopped.

## Versioning and exports

No `.quality/**` artifact is intended for source control, including guidelines,
scope rules, security baselines, findings, sidecars, usage ledgers, run state,
sensor inventories, and canonical run reports. `.gitignore` therefore ignores
`.quality/` at every depth.

Use `GET /api/report?format=markdown|html|json|sarif` or
`GET /api/review/runs/{id}/report?format=...` to export a deliberate snapshot.
The standalone report command can likewise write to an explicit `--output` path.
Only that chosen export is suitable for publication or versioning.
