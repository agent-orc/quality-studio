namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// A host-owned analyzer execution profile. Repository configuration selects a profile by id; it can never
/// supply the executable or the argument template, so repository registration cannot reach host command execution.
/// </summary>
public sealed record AnalyzerProfile(
    string Id,
    string SensorId,
    string Executable,
    IReadOnlyList<string> Arguments,
    string Description);

/// <summary>The outcome of matching repository sensor configuration against the host-owned profile catalog.</summary>
public sealed record AnalyzerProfileResolution(AnalyzerProfile? Profile, string? RejectionReason)
{
    /// <summary>No profile was requested; the sensor ingests a report produced outside Quality Studio.</summary>
    public static AnalyzerProfileResolution None { get; } = new(null, null);

    public bool IsRejected => RejectionReason is not null;
}

/// <summary>
/// The immutable set of analyzer executables Quality Studio is willing to start, keyed by sensor and profile id.
/// Command-backed profiles are disabled by default and stay disabled until the review worker boundary exists.
/// </summary>
public sealed class AnalyzerProfileCatalog
{
    /// <summary>Repository sensor configuration key that selects a host-owned profile.</summary>
    public const string ProfileKey = "profile";

    /// <summary>Repository sensor configuration key that used to carry a free-form command line.</summary>
    public const string CommandKey = "command";

    public const string CommandRejectionReason =
        "Repository-configured analyzer commands are not executed. Select a host-owned analyzer profile with " +
        "'profile' instead of 'command'.";

    public const string CommandProfilesDisabledReason =
        "Command-backed analyzer profiles are disabled. Set QualityStudio:Security:AllowCommandAnalyzerProfiles " +
        "only for repositories the Studio operator controls.";

    private static readonly AnalyzerProfile[] BuiltIn =
    [
        new("dotnet-build-sarif", "roslyn", "dotnet",
            ["build", "{target}", "--nologo", "--verbosity", "quiet", "/p:ErrorLog={reportPath},version=2.1"],
            "Builds the target with the repository's own Roslyn analyzers and writes a SARIF 2.1 error log."),
        new("eslint-sarif", "eslint", "npx",
            ["--no-install", "eslint", "{target}", "--format", "@microsoft/eslint-formatter-sarif",
                "--output-file", "{reportPath}"],
            "Runs the repository's installed ESLint with the SARIF formatter."),
        new("tsc-noemit", "tsc", "npx",
            ["--no-install", "tsc", "--noEmit", "--pretty", "false"],
            "Type-checks the working directory with the repository's installed TypeScript compiler."),
    ];

    public AnalyzerProfileCatalog(bool commandProfilesEnabled = false) =>
        CommandProfilesEnabled = commandProfilesEnabled;

    /// <summary>The default catalog: profiles are known and selectable, but none of them may start a process.</summary>
    public static AnalyzerProfileCatalog Disabled { get; } = new();

    public bool CommandProfilesEnabled { get; }

    public IReadOnlyList<AnalyzerProfile> For(string sensorId) => BuiltIn
        .Where(profile => string.Equals(profile.SensorId, sensorId, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>
    /// Validates repository-supplied sensor configuration without regard to whether execution is currently
    /// permitted, so a rejected registration explains the configuration error rather than the operating mode.
    /// </summary>
    public string? Validate(string sensorId, IReadOnlyDictionary<string, string>? configuration)
    {
        if (configuration is null) return null;
        if (configuration.TryGetValue(CommandKey, out var command) && !string.IsNullOrWhiteSpace(command))
            return CommandRejectionReason;
        if (!configuration.TryGetValue(ProfileKey, out var profileId) || string.IsNullOrWhiteSpace(profileId))
            return null;
        return Find(sensorId, profileId) is null ? UnknownProfileReason(sensorId, profileId) : null;
    }

    public AnalyzerProfileResolution Resolve(string sensorId, IReadOnlyDictionary<string, string>? configuration)
    {
        if (Validate(sensorId, configuration) is { } rejection) return new AnalyzerProfileResolution(null, rejection);
        if (configuration is null ||
            !configuration.TryGetValue(ProfileKey, out var profileId) ||
            string.IsNullOrWhiteSpace(profileId))
            return AnalyzerProfileResolution.None;
        return CommandProfilesEnabled
            ? new AnalyzerProfileResolution(Find(sensorId, profileId), null)
            : new AnalyzerProfileResolution(null, CommandProfilesDisabledReason);
    }

    private static AnalyzerProfile? Find(string sensorId, string profileId) => BuiltIn.FirstOrDefault(profile =>
        string.Equals(profile.SensorId, sensorId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(profile.Id, profileId.Trim(), StringComparison.OrdinalIgnoreCase));

    private string UnknownProfileReason(string sensorId, string profileId)
    {
        var available = For(sensorId).Select(profile => profile.Id).ToArray();
        return available.Length == 0
            ? $"Sensor '{sensorId}' has no host-owned analyzer profiles; it can only ingest an existing report."
            : $"Unknown analyzer profile '{profileId.Trim()}' for sensor '{sensorId}'. " +
              $"Available profiles: {string.Join(", ", available)}.";
    }
}
