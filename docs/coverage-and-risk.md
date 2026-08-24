# Coverage and risk

Coverage ingestion is the `coverage` repository sensor. It reads existing reports; it never starts a test run.

Configure one or more repository-relative paths in the repository sensor configuration. Separate paths or glob patterns with `;`:

```json
{
  "id": "coverage",
  "enabled": true,
  "configuration": {
    "reportPaths": "artifacts/coverage.cobertura.xml;frontend/coverage/lcov.info;TestResults/**/*.trx"
  }
}
```

Run ingestion with `POST /api/repos/{repoId}/sensors/coverage/scan`. Cobertura XML, lcov, Visual Studio `.trx` attachments, XML `.coverage` reports, and native binary `.coverage` reports are supported. Native reports are converted with `dotnet-coverage merge` when that tool is available; conversion does not run tests.

The sensor writes `.quality/coverage/coverage.json` in the repository. The snapshot records the measured commit and timestamp. A successful scan with no matching reports writes an empty snapshot, so every API and UI surface returns `unknown`, not an assumed `0%`.

`GET /api/repos/{repoId}/risk?days=90` combines the code-review grade, line coverage, and the number of Git commits touching each file in the requested window. Missing grade or coverage keeps the combined risk score unknown. The response also includes compact grade-by-coverage matrix cells.

## Source paths in reports

Report formats express source paths differently, and a path that does not resolve to a
real repository file silently covers nothing. Resolution therefore runs in this order:

1. An absolute path inside the repository is made relative to the repository root.
2. A relative path that exists directly under the repository root is used as is.
3. Cobertura filenames are combined with the report's declared `<sources><source>` roots.
   Coverlet writes `<source>` as `<repo>/src/` with filenames like `Project/File.cs`.
4. Relative paths are resolved against the report's own directory and then each parent up
   to the repository root. lcov has no source-root declaration, and the Angular Karma run
   writes `src/app/...` from `frontend/`.
5. Only if all of the above fail are leading path segments dropped until something matches.

## Coverage ratchet

`quality coverage` measures line coverage per area from generated reports and compares it
with the committed baseline in `.quality/coverage-baseline.json`.

```shell
dotnet run --project src/quality-cli -- coverage . --report .coverage/dotnet --report frontend/coverage
dotnet run --project src/quality-cli -- coverage . --report .coverage/dotnet --report frontend/coverage --update
```

Collection uses `tests/coverage.runsettings`, which excludes `**/obj/**`, `*.g.cs`, and
members carrying `GeneratedCodeAttribute` or `CompilerGeneratedAttribute`. Without it the
measurement is dominated by source-generator output — for this repository the emitted
regex code alone is several thousand lines — and the ratchet would move whenever the SDK
changes what it emits rather than when tests change.

A `--report` argument may be a file or a directory; directories are searched recursively
for `*.cobertura.xml`, `*.info`, and `*.lcov`, because `dotnet test` writes coverage under
a generated per-run subdirectory. Exit codes follow the other gate commands: `0` no area
regressed, `1` an area fell below its baseline beyond the recorded tolerance, `2` a report
or the baseline was missing or unreadable.

Two failures are deliberately not treated as coverage results. A `--report` path that does
not exist fails with exit `2`, and an area that measures zero lines while its baseline
recorded more fails with exit `1` — a collector that never ran must not read as a pass.
The baseline is only ever produced by `--update` from real reports; no percentage is
asserted before it has been measured.
