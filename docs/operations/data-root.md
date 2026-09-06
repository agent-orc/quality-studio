# Quality Studio project data root

## Contract

An analyzed repository is an input boundary, not a persistence directory. Quality
Studio reads source and Git state from the configured repository root. It writes
reviews, findings, reports, usage ledgers, coverage, run state, analyzer output,
project inputs, rule overrides, and other `.quality/**` state beneath a separate
project data root. Normal scans and reviews do not create or update files under the
checkout.

API contracts retain logical `.quality/...` paths so existing links and schemas stay
portable. `QualityDataRoot` translates those logical paths to physical paths beneath
the external project directory.

## Resolution

The default base directory is:

- Windows: `%LOCALAPPDATA%\QualityStudio\projects\`
- Linux and macOS: the runtime value returned for the platform's local application
  data directory, followed by `QualityStudio/projects/`

The API appends its stable repository id, producing
`<base>/<project-id>/`. Configure the base with `QualityStudio:DataRoot` in API
configuration. Standalone library and CLI processes can set
`QUALITY_STUDIO_DATA_ROOT`. When no API registration exists, the resolver derives a
stable key from the repository name and a SHA-256 digest of `remote.origin.url`, or
of the canonical repository path when no origin exists.

The configured project directory must be separate from the repository checkout.
Startup fails rather than accepting a data directory inside the checkout.

## One-time migration

API startup checks every registered Git repository for root or nested `.quality`
content and logs warning event `1410` when it finds tracked, untracked, or ignored
paths. It then migrates legacy files:

1. Root `.quality/**` paths keep the same relative layout in the project data root.
2. Nested review sidecars are consolidated under `reviews/`.
3. Other nested data is retained under `legacy/<repository-relative-owner>/`.
4. An existing identical destination is deduplicated. A different destination is
   preserved under `migration-conflicts/` instead of being overwritten.
5. Empty legacy directories are removed and `.migration-v1.json` records completion.

Copy-then-delete behavior supports different checkout and data-root volumes. If a
migration stops before the marker is written, the next startup safely retries it.
Back up the external project directory with the same policy used for other local
application data.

## Source control and exports

No runtime data in the project root is intended for source control. `.gitignore`
ignores `.quality/` at every depth. Quality Studio does not stage, commit, merge,
push, or otherwise mutate Git state.

To retain a report in a repository, explicitly export it to a non-`.quality` path:

```shell
dotnet run --project src/quality-cli -- report . --format html --output docs/quality-report.html
dotnet run --project src/quality-cli -- diff . --base main --head HEAD --format json --output docs/quality-diff.json
```

Those files are ordinary user-managed exports. Nothing is versioned automatically.

## Operational checks

After migration, run `git status --short` in the integration checkout. A full review
must leave the result unchanged. If startup warning `1410` recurs, inspect the named
path for an old process or custom integration that still writes `.quality` inside the
checkout, then move the data to the registered project data root.
