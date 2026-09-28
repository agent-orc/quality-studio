# Coverage and risk

Coverage ingestion is the `coverage` repository sensor. By default it reads existing reports and never
starts a test run. A repository can opt into a host-owned **producer** that runs the tests first — see
[Producing coverage](#producing-coverage).

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

The sensor writes `coverage/coverage.json` below the project's data root, not into the repository it measured ([`data-root.md`](data-root.md)). The snapshot records the measured commit and timestamp. A successful scan with no matching reports writes an empty snapshot, so every API and UI surface returns `unknown`, not an assumed `0%`.

## Producing coverage

Selecting a host-owned profile in the coverage configuration makes the sensor run the tests before it
ingests. It is opt-in per repository — without `profile` nothing runs — and only a profile the host
declares can be selected ([`deployment.md`](deployment.md#analyzer-profiles)):

```json
{
  "id": "coverage",
  "enabled": true,
  "configuration": { "profile": "dotnet-test-coverage", "target": "AgentStudio.slnx" }
}
```

| Profile | Command | Time-box |
| --- | --- | --- |
| `dotnet-test-coverage` | `dotnet test {target} --collect "Code Coverage;Format=cobertura" --results-directory {outputDirectory}` | 900 s |
| `vitest-frontend-coverage` | `npx --no-install vitest run --coverage.enabled=true --coverage.reporter=lcov …` in `frontend/` | 600 s |
| `vitest-root-coverage` | the same vitest command in the repository root | 600 s |

`target` (default: the repository root) and `workingDirectory` stay confined to the repository.
`{outputDirectory}` is `coverage/produced/<profile>/` below the project's data root, emptied before each
run so an earlier report can never pass for this one; the checkout stays clean. When the time-box
expires the process tree is killed and the scan is unavailable. A producer that fails - timed out, not
launchable, or writing no report - leaves the previous snapshot in place: measured coverage is never
replaced by unknown. Failing tests still produce coverage, so a non-zero exit code with reports is
ingested and recorded in the snapshot's `production` (`profile`, `exitCode`, `elapsedSeconds`,
`timeoutSeconds`). Reports from a producer are listed in `reports` with a `data-root:` prefix. vitest
writes lcov paths relative to its own root; they are resolved against the profile's working directory.
With a profile, the default report patterns are not scanned; explicitly configured `reportPaths` are
still ingested next to the producer's output.

Run it like any sensor: `POST /api/repos/{repoId}/sensors/coverage/scan`. The response's provenance
carries `producer`, `producerExitCode` and `producerSeconds`.

## Complexity

The risk view measures cyclomatic and cognitive complexity per function and per file for C#
(Roslyn syntax tree, no compilation) and TypeScript/JavaScript (a tokenizer, no Node process). Both
follow one rule set:

- **Cyclomatic**: 1 + each `if`, loop, `case`, `catch`, conditional operator, `&&`, `||`, `??` (and C#
  `and`/`or` patterns and non-discard switch-expression arms).
- **Cognitive** (SonarSource definition): +1 per `if`, `else if`, `else`, `switch`, loop, `catch` and
  conditional operator, plus the current nesting depth for the nesting ones; +1 per sequence of like
  `&&`/`||` operators; lambdas, callbacks and nested functions add a nesting level and fold into the
  function that declares them.

A function unit is a C# method, constructor, accessor, operator or top-level local function; in
TypeScript a class member or top-level declaration holding function code. Statements outside any
function form one `<top-level>` unit. File values sum their functions. `.d.ts`, `.tsx` and `.jsx` are not
measured, and a brace-less `if` body adds no nesting in TypeScript — the tokenizer's known limits.
Results are cached per file by size and write time.

A file's complexity **pressure** is its most complex function against twice the threshold of 15:
`min(100, maxCognitive × 100 / 30)`. The maximum, not the sum: one tangled function is what makes a file
hard to change and to test.

## Risk

`GET /api/repos/{repoId}/risk?days=90` combines the code-review grade, line coverage, complexity, and the
number of Git commits touching each file in the requested window:

| Component | Measured file | File without complexity (not C#/TS/JS) |
| --- | --- | --- |
| `100 − grade` | × 0.3 | × 0.4 |
| `100 − line coverage %` | × 0.3 | × 0.4 |
| complexity pressure | × 0.2 | — |
| churn | up to 20 points, relative to the most-changed file | up to 20 points |

Missing grade or coverage keeps the combined risk score unknown - complexity alone never makes a score.
The response also includes compact grade-by-coverage matrix cells.

Directories show a size-weighted projection of their files' grades until an aggregate review grades them;
see [`hierarchy-aggregation.md`](hierarchy-aggregation.md#grade-projection).
