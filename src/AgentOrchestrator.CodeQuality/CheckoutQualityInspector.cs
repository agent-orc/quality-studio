namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// What the studio's own writes have left behind in an analysed checkout.
/// <see cref="GeneratedInTree"/> is data a pre-data-root run wrote and the migration has not moved
/// yet; <see cref="DirtyPaths"/> is what Git currently reports as uncommitted below a
/// <c>.quality</c> folder, which is the exact condition Agent Studio refuses to integrate into.
/// </summary>
public sealed record CheckoutQualityStatus(
    string RepositoryRoot,
    bool IsGitCheckout,
    IReadOnlyList<string> GeneratedInTree,
    IReadOnlyList<string> DirtyPaths)
{
    public bool NeedsAttention => GeneratedInTree.Count > 0 || DirtyPaths.Count > 0;

    /// <summary>One operator-facing sentence naming the condition and the way out of it.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (GeneratedInTree.Count > 0)
            parts.Add(
                $"{GeneratedInTree.Count} generated artefact(s) still live in the checkout " +
                $"({Sample(GeneratedInTree)}). Run 'quality migrate-data \"{RepositoryRoot}\"' to move them " +
                "to the project's data root, then commit the deletion once.");
        if (DirtyPaths.Count > 0)
            parts.Add(
                $"Git reports {DirtyPaths.Count} uncommitted path(s) below a .quality folder " +
                $"({Sample(DirtyPaths)}). Agent Studio refuses to fast-forward a checkout with " +
                "uncommitted changes, so this blocks integration until it is committed or removed.");
        return parts.Count == 0
            ? $"The .quality tree of '{RepositoryRoot}' is clean."
            : string.Join(" ", parts);
    }

    private static string Sample(IReadOnlyList<string> paths) =>
        paths.Count <= 3
            ? string.Join(", ", paths)
            : string.Join(", ", paths.Take(3)) + $", and {paths.Count - 3} more";
}

/// <summary>
/// Reports whether the studio is about to dirty, or has already dirtied, the checkout it analyses.
/// The studio writes its own artefacts elsewhere now, so anything this finds is either data from
/// before that change or a foreign writer - both worth naming at startup rather than discovering
/// when an integration fails.
/// </summary>
public static class CheckoutQualityInspector
{
    public static async Task<CheckoutQualityStatus> InspectAsync(
        string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var generated = Directory.Exists(root)
            ? QualityDataMigration.RemainingInTree(root)
            : [];

        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            return new CheckoutQualityStatus(root, IsGitCheckout: false, generated, []);

        try
        {
            var status = await GitPlumbing
                .RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken)
                .ConfigureAwait(false);
            return new CheckoutQualityStatus(root, IsGitCheckout: true, generated, DirtyQualityPaths(status));
        }
        catch (Exception exception) when (exception is ChangeReviewException or IOException)
        {
            // A checkout Git cannot read is not a reason to refuse to start; the generated-data
            // half of the report is filesystem-only and still stands.
            return new CheckoutQualityStatus(root, IsGitCheckout: false, generated, []);
        }
    }

    /// <summary>
    /// The paths a status reports below a <c>.quality</c> folder. A rename contributes both of its
    /// paths: a file moved out of <c>.quality</c> is a change to <c>.quality</c> and blocks a
    /// fast-forward exactly like a file moved into it.
    /// </summary>
    private static IReadOnlyList<string> DirtyQualityPaths(string status) =>
        GitStatusPaths.Parse(status)
            .Where(path => path.StartsWith(QualityDataRoot.LegacyDirectoryName + "/", StringComparison.Ordinal) ||
                           path.Contains("/" + QualityDataRoot.LegacyDirectoryName + "/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
