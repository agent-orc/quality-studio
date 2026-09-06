namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Builds sidecars that satisfy the review metadata contract. Fixtures used to hand-write partial
/// documents, which every reader then had to tolerate; they now write what the product writes.
/// </summary>
internal static class ReviewMetaFixture
{
    public static ReviewMetaDocument Document(
        string unitId,
        string relativePath,
        string reviewedHash,
        IReadOnlyList<SubjectInputHash> subjectInputs,
        ReviewAdapter adapter = ReviewAdapter.Generic,
        ReviewLevel level = ReviewLevel.File,
        ReviewKind kind = ReviewKind.Code,
        int score = 90,
        string rationale = "Fixture grade.",
        string? effectiveHash = null,
        string model = "fixture-model",
        DateTimeOffset? reviewedAt = null,
        IReadOnlyList<ReviewFinding>? findings = null,
        IReadOnlyList<ReviewThread>? threads = null) => new()
    {
        Unit = new ReviewUnit(unitId, adapter, level, relativePath, Path.GetFileName(relativePath)),
        ReviewedAt = reviewedAt ?? new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
        Kind = kind,
        Reviewer = new ReviewerIdentity("fixture-agent", model),
        ReviewedHash = ManifestHash.Subject(reviewedHash),
        SubjectInputs = subjectInputs,
        ReviewInputs = new ReviewInputs(
            ManifestHash.ReviewInput(effectiveHash ?? new string('a', 64)),
            true,
            [],
            [],
            new PromptReference($"file-{kind.ToString().ToLowerInvariant()}-review", "1.0.0",
                "sha256:" + new string('b', 64))),
        Grade = new ReviewGrade(score, Band(score), rationale),
        Summary = "A fixture review.",
        Aspects = [new ReviewAspect("correctness", "Correctness", new ReviewGrade(score, Band(score), rationale))],
        Findings = findings ?? [],
        Threads = threads ?? [],
    };

    public static ReviewFinding Finding(
        string fingerprint,
        string ruleId = "quality.test",
        FindingSeverity severity = FindingSeverity.High,
        string title = "Fixture finding",
        string path = "src/App.cs",
        FindingRange? range = null) => new(
        "finding-" + fingerprint[7..],
        "correctness",
        severity,
        title,
        "A deterministic fixture finding.",
        "Fix the fixture.",
        [new FindingLocation(path, range)],
        fingerprint,
        ruleId);

    /// <summary>The review-input hash today's standards and prompt produce, so a fixture reads fresh.</summary>
    public static string EffectiveHash(string repositoryRoot, string kind = "code", ReviewLevel level = ReviewLevel.File) =>
        new InputResolver().Resolve(repositoryRoot, kind, level)
            .EffectiveHash(ReviewPromptBuilder.TemplateHash(kind));

    public static GradeBand Band(int score) => score switch
    {
        >= 90 => GradeBand.A,
        >= 80 => GradeBand.B,
        >= 70 => GradeBand.C,
        >= 60 => GradeBand.D,
        _ => GradeBand.F,
    };

    public static Task WriteAsync(string path, ReviewMetaDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return File.WriteAllTextAsync(path, ReviewMetaJson.Serialize(document), cancellationToken);
    }
}
