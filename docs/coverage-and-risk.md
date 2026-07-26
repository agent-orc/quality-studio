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
