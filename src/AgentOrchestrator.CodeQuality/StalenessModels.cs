namespace AgentOrchestrator.CodeQuality;

public sealed record FileStaleness(
    string RelativePath,
    StalenessState State,
    string ReviewKind,
    string? MetaRelativePath = null);

public sealed record StalenessReport(IReadOnlyList<FileStaleness> Files)
{
    public int FreshCount => Files.Count(file => file.State == StalenessState.Fresh);
    public int StaleCount => Files.Count(file => file.State == StalenessState.Stale);
    public int MissingCount => Files.Count(file => file.State == StalenessState.Missing);
}

public sealed record StalenessEvaluatorOptions
{
    public IReadOnlyList<string> IncludeGlobs { get; init; } =
    [
        "**/*.cs", "**/*.csx", "**/*.fs", "**/*.fsx", "**/*.vb",
        "**/*.ts", "**/*.tsx", "**/*.js", "**/*.jsx", "**/*.mjs", "**/*.cjs",
        "**/*.html", "**/*.css", "**/*.scss", "**/*.razor", "**/*.cshtml",
        "**/*.py", "**/*.go", "**/*.rs", "**/*.java", "**/*.kt", "**/*.kts",
        "**/*.c", "**/*.cc", "**/*.cpp", "**/*.h", "**/*.hpp",
    ];

    public string ReviewKind { get; init; } = "code";
}

public sealed class StalenessScanException(string message, Exception? innerException = null)
    : Exception(message, innerException);
