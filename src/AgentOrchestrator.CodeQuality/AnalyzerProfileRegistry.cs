namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// A host-owned, immutable analyzer profile: a fixed executable and argument template. A repository-scoped
/// client can select a profile by id, but the id is the only thing it ever influences -- the executable and
/// argument list come entirely from host configuration. This is the replacement for a caller-supplied
/// free-form "command" string (docs/operations/security/index.html, S0): a profile-backed scan can never
/// execute attacker-controlled code, so it does not need the AllowCommandBackedAnalyzers opt-in.
/// </summary>
public sealed record AnalyzerProfile(string Id, string Executable, IReadOnlyList<string> Arguments)
{
    public static AnalyzerProfile Create(string id, string executable, params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Analyzer profile id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Analyzer profile executable is required.", nameof(executable));
        if (arguments.Length == 0)
            throw new ArgumentException("Analyzer profile arguments are required.", nameof(arguments));
        return new AnalyzerProfile(id.Trim().ToLowerInvariant(), executable, arguments);
    }

    public IReadOnlyList<string> Expand(string repositoryRoot, string target, string reportPath) =>
        Arguments.Select(argument => argument
                .Replace("{repositoryRoot}", repositoryRoot, StringComparison.Ordinal)
                .Replace("{target}", target, StringComparison.Ordinal)
                .Replace("{reportPath}", reportPath, StringComparison.Ordinal))
            .ToArray();
}

/// <summary>Resolves a host-approved analyzer profile id to its immutable executable and argument template.</summary>
public sealed class AnalyzerProfileRegistry
{
    public const string EslintSarifProfileId = "eslint-sarif";

    public static AnalyzerProfile EslintSarifProfile { get; } = AnalyzerProfile.Create(
        EslintSarifProfileId, "node",
        "frontend/node_modules/eslint/bin/eslint.js", ".",
        "--config", "frontend/eslint.config.mjs",
        "--format", "frontend/node_modules/@microsoft/eslint-formatter-sarif/sarif.js",
        "--output-file", "{reportPath}");

    private readonly IReadOnlyDictionary<string, AnalyzerProfile> profiles;

    public AnalyzerProfileRegistry(IEnumerable<AnalyzerProfile>? hostProfiles = null)
    {
        var merged = new Dictionary<string, AnalyzerProfile>(StringComparer.Ordinal)
        {
            [EslintSarifProfile.Id] = EslintSarifProfile,
        };
        foreach (var profile in hostProfiles ?? [])
        {
            merged[profile.Id] = profile;
        }
        profiles = merged;
    }

    public bool Contains(string id) => profiles.ContainsKey(Normalize(id));

    public bool TryGet(string id, out AnalyzerProfile profile) => profiles.TryGetValue(Normalize(id), out profile!);

    public IReadOnlyList<AnalyzerProfile> List() =>
        profiles.Values.OrderBy(profile => profile.Id, StringComparer.Ordinal).ToArray();

    private static string Normalize(string id) => id.Trim().ToLowerInvariant();
}
