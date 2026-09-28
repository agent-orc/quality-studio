# Quality Studio change history

Product-level history. The rule library keeps its own in [`rules/CHANGELOG.md`](rules/CHANGELOG.md).

## Unreleased

### Changed — analyzer rules are catalogue citizens (QS-113)

The Roslyn, ESLint and tsc profiles failed on real repositories in the 2026-09-28 evaluation against
the Agent Studio checkout (defects D8-D10). Fixed:

- **Roslyn**: `-p:ErrorLog=…,version=2.1` was split by MSBuild at the comma into a SARIF 1.0 log the
  importer rejected, every project of a solution wrote the same file, and an incremental build logged
  nothing. The profile now forces a compile (`--no-incremental`) and hands MSBuild a generated import that
  gives every project its own SARIF 2.1 log (`%2C`-escaped); the sensor merges the log directory.
- **tsc**: runs the workspace's own compiler through `node` with `-p`, and checks each project a
  solution-style `tsconfig.json` references (`tsc -p` on Angular's root config compiled nothing).
- **ESLint**: the binary, the formatter and the flat config are resolved from the workspace, including
  hoisted `node_modules`, and the `frontend` profile runs in `frontend/`.
- **Probes** run in the analysed repository and the profile's working directory instead of the host's.
- The SARIF import honours `suppressions` and reports `suppressedFindings`.

Deterministic sensor results are persisted in the data root (`analyzers/<sensor>.json`) by scans and by
review evidence collection, shown as badges in the explorer and as an analyzer strip and gutter marks in
the editor (`GET /api/repos/{id}/analyzers`, `…/analyzers/counts`, and `analyzers` on the file response).

Analyzer ids map to named rules through `deterministicRuleIds` (rule library 1.6.0): twelve rules list the
Roslyn, compiler, ESLint and Angular compiler ids that check them, and two opt-in family rules own
`CS*`/`CA*`/`IDE*` (QS-CS-013) and `TS*` (QS-NG-014). A linked finding reaches the review agent with
`catalogueRuleIds`, so one rule is enforced by the analyzer and explained by the agent.

### Changed — the studio no longer writes into the checkout it analyses (QS-102)

Everything a run generates now lives in a per-project **data root** outside the analysed working
copy: `%LOCALAPPDATA%\QualityStudio\projects\<project-key>\` by default, overridable with
`QualityStudio:DataRoot` or `QUALITY_STUDIO_DATA_ROOT`. The container image and `docker-compose.yml`
point it at the `/data` volume that was already reserved for this.

Moved out of the checkout: review sidecars, `findings/`, `reports/`, `usage/`, `boundaries/`,
`coverage/`, `changes/`, `flows/`, `runs/` and the attack coverage ledger. Author-owned inputs stay
where they are and are still versioned — `.quality/scope.json`, `.quality/inputs/`,
`.quality/rules/overrides.json`, `.quality/security/` and `.quality/attacks/catalogue.json` — as does
`.quality/preflight/`, which an external analyzer writes under the working directory it runs in.

Review sidecars changed shape as well as location: instead of a `.quality/reviews` folder beside
every reviewed file, one `reviews/` lane in the data root mirrors the subject's directory.

Why: this repository is both the integration checkout of the QS project in Agent Studio and a
project the studio analyses. Every run left the working tree dirty, Agent Studio refuses to
fast-forward a dirty checkout, and QS-85 failed on that on 2026-08-27. Committing 139 generated
files on 2026-09-06 unblocked nine deliveries but was a workaround, not the rule.

### Added

- `quality migrate-data [path] [--dry-run]` moves an existing checkout's generated `.quality` data
  to its data root. Idempotent, refuses to overwrite what the data root already holds, and leaves
  inputs alone.
- The API warns at startup when a registered project root still carries generated studio data or has
  uncommitted changes below a `.quality` folder — the exact condition that blocks integration.

### Fixed

- The live report no longer grades files that are absent from the checked-out branch. Sidecars used
  to be committed, so a branch switch changed which of them existed; the data root is shared across
  branches, and without this a branch that deleted a file would still be graded on it.

### Removed

- Monthly usage ledgers and `.quality/reports/runs/*.json` are no longer versioned. `.gitignore`
  ignores everything under `.quality` and names the author-owned inputs back in. A report meant to
  be shared is exported deliberately with `quality report --output`.
- `.gitattributes` held one rule, `merge=union` for the in-tree usage ledgers. Those ledgers are no
  longer in the tree, so the file is gone.

### Upgrading

`.gitignore` does not untrack a file that is already committed, so an existing checkout needs one
migration and one commit:

```shell
dotnet run --project src/quality-cli -- migrate-data . --dry-run
dotnet run --project src/quality-cli -- migrate-data .
git commit -am "chore(quality): move generated studio data out of the checkout"
```

Two known consequences, both from features that read sidecars out of Git history and therefore can
only see the layout that history was written in:

- The score trend in `quality report` keeps every point up to the migration commit and gains none
  after it. Live grades are read from the data root and are unaffected.
- A change review over a range whose endpoints are both after the migration reports no agent-grade
  movement. Everything it derives from the diff — touched units, boundary and coverage facts,
  evidence economy — is unaffected, and a range spanning older commits still reads their sidecars.

See [`docs/data-root.md`](docs/data-root.md).
