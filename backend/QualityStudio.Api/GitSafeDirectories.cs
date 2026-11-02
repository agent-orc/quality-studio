namespace QualityStudio.Api;

/// <summary>
/// The container runs as its own user while the mounted checkouts belong to the account that
/// cloned them, and Git refuses a repository owned by another user unless <c>safe.directory</c>
/// names it. With <c>QualityStudio:Git:TrustRegisteredRoots</c> (set by the image) the host hands
/// every registered repository root to the Git processes it starts, as command-scope configuration
/// through <c>GIT_CONFIG_COUNT</c>, which Git honours for <c>safe.directory</c>. The image's Git
/// (2.43) matches exact paths only, so the entries follow the registry: a repository registered at
/// run time is trusted from its registration on, and nothing outside the registry is.
/// </summary>
public sealed class GitSafeDirectories
{
    public const string ConfigurationKey = "QualityStudio:Git:TrustRegisteredRoots";
    private const string CountVariable = "GIT_CONFIG_COUNT";
    private readonly object gate = new();
    private readonly IReadOnlyList<(string Key, string Value)> inherited;
    private int published;

    public GitSafeDirectories(IConfiguration configuration)
    {
        Enabled = configuration.GetValue<bool>(ConfigurationKey);
        inherited = Inherited();
        published = inherited.Count;
    }

    public bool Enabled { get; }

    /// <summary>Replaces the trusted set with <paramref name="roots"/>; configuration the host inherited is kept ahead of it.</summary>
    public void Trust(IEnumerable<string> roots)
    {
        if (!Enabled) return;
        var entries = inherited
            .Concat(roots.Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(root => ("safe.directory", root)))
            .ToArray();
        lock (gate)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{index}", entries[index].Item1);
                Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{index}", entries[index].Item2);
            }
            Environment.SetEnvironmentVariable(CountVariable, entries.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (var index = entries.Length; index < published; index++)
            {
                Environment.SetEnvironmentVariable($"GIT_CONFIG_KEY_{index}", null);
                Environment.SetEnvironmentVariable($"GIT_CONFIG_VALUE_{index}", null);
            }
            published = entries.Length;
        }
    }

    private static IReadOnlyList<(string Key, string Value)> Inherited()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(CountVariable), out var count) || count <= 0) return [];
        var entries = new List<(string, string)>();
        for (var index = 0; index < count; index++)
        {
            var key = Environment.GetEnvironmentVariable($"GIT_CONFIG_KEY_{index}");
            if (string.IsNullOrEmpty(key)) break;
            entries.Add((key, Environment.GetEnvironmentVariable($"GIT_CONFIG_VALUE_{index}") ?? string.Empty));
        }
        return entries;
    }
}
