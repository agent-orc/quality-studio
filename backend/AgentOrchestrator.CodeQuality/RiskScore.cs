namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The combined 0-100 file risk of the risk view: review grade, line coverage, complexity pressure and
/// Git churn. Grade and coverage stay required - without either the score is unknown, never a guess.
/// Complexity is optional because only C#, TypeScript and JavaScript are measured; a file without it
/// keeps the original grade/coverage/churn weights, so its score stays comparable with earlier runs.
/// </summary>
public static class RiskScore
{
    public const decimal GradeWeight = 0.3m;
    public const decimal CoverageWeight = 0.3m;
    public const decimal ComplexityWeight = 0.2m;
    public const decimal ChurnPoints = 20m;

    /// <summary>Grade and coverage weights for a file whose complexity is not measured.</summary>
    public const decimal UnmeasuredComplexityWeight = 0.4m;

    public static decimal? Combine(int? grade, decimal? linePercent, int? complexityPressure, int changes, int maxChanges)
    {
        if (grade is null || linePercent is null) return null;
        var churn = changes * ChurnPoints / Math.Max(1, maxChanges);
        var score = complexityPressure is int pressure
            ? (100 - grade.Value) * GradeWeight + (100 - linePercent.Value) * CoverageWeight +
              pressure * ComplexityWeight + churn
            : (100 - grade.Value) * UnmeasuredComplexityWeight + (100 - linePercent.Value) * UnmeasuredComplexityWeight +
              churn;
        return Math.Round(score, 2, MidpointRounding.AwayFromZero);
    }
}
