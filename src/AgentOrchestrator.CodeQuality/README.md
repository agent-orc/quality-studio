# AgentOrchestrator.CodeQuality

`AgentOrchestrator.CodeQuality` is the in-process analysis core used by Quality
Studio. It lets a server-side pipeline, command-line tool, or CI process run named
analyses over a repository path and receive Quality Studio `ReviewFinding` values
without starting the Quality Studio HTTP API.

The package targets .NET 10. It contains the stable analysis facade, finding and
review contracts, repository hierarchy and staleness primitives, built-in sensors,
and the review execution primitives used by standalone consumers. It has no
ASP.NET Core or Quality Studio UI dependency.

## Quick start

```csharp
using AgentOrchestrator.CodeQuality;

var core = QualityAnalysisCore.CreateDefault();
var result = await core.RunAsync(new QualityAnalysisRequest(
    RepositoryPath: repositoryPath,
    Analyses:
    [
        new NamedQualityAnalysis(QualityAnalysisNames.Boundaries),
        new NamedQualityAnalysis(
            QualityAnalysisNames.Dependencies,
            Configuration: new Dictionary<string, string>
            {
                ["ecosystems"] = "dotnet",
            }),
    ]));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.Title}");
}
```

`PersistArtifacts` defaults to `false`, which is appropriate for pipeline and CI
callers. Set it to `true` only when the caller intentionally wants the analysis to
write repository-owned `.quality` artifacts. Repository/worktree Git operations
remain the responsibility of the hosting backend or pipeline; this package does
not commit, push, merge, or create worktrees.

## Built-in names

`QualityAnalysisCore.CreateDefault()` registers:

- `boundaries`
- `coverage`
- `dependencies`
- `eslint`
- `gitleaks`
- `roslyn`
- `sarif`
- `tsc`

Some analyses require external tools or explicit configuration. An unavailable
tool is represented by the named result's `Available` and `UnavailableReason`
fields rather than by an HTTP response. Run only the analyses the host selects.

## Findings and rule content

Named analyses return the Quality Studio `ReviewFinding` model together with
availability and provenance. A consumer that needs the cross-system envelope can
convert a finding with `QualityFindingEnvelope.FromReviewFinding` once it has the
subject identity required by that contract.

The core owns execution contracts, parsing, hashing, and provenance. Review rules
and guidelines are input content: repository rules come from `.quality/inputs`,
global inputs are supplied by the host, and the QS-90 rule library remains
independently versioned content consumed through configuration. Rule libraries
are not dependencies of the analysis facade.

## Hosting boundary

The Quality Studio API references this package and translates its results to HTTP.
Agent Studio handover, authentication, rate limiting, API DTOs, background hosting,
and the Angular UI are not part of this package.

The package is licensed under Apache-2.0. Package publication is a separate release
operation; building this repository only creates local package artifacts.
