using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One host-owned analyzer invocation. Repository configuration selects it by <see cref="Id"/> and can
/// never supply the command itself, so an API client cannot turn sensor configuration into arbitrary
/// host execution. <c>{repositoryRoot}</c>, <c>{target}</c> and <c>{reportPath}</c> are expanded at run time.
/// </summary>
public sealed record AnalyzerProfile(
    string Id,
    string Sensor,
    string Command,
    string? WorkingDirectory = null,
    string? ReportPath = null,
    string? Description = null);

/// <summary>The analyzer profiles this host offers, and whether it still accepts inline commands at all.</summary>
public sealed class AnalyzerProfileCatalog
{
    /// <summary>File name the API looks for when <c>QualityStudio:AnalyzerProfiles:Path</c> is unset.</summary>
    public const string DefaultFileName = "analyzer-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<AnalyzerProfile> profiles;

    public AnalyzerProfileCatalog(IEnumerable<AnalyzerProfile> profiles, bool allowInlineCommands = false)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var validated = profiles.Select(Validate).ToArray();
        var duplicate = validated.GroupBy(profile => (profile.Sensor, profile.Id), SensorProfileComparer)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Analyzer profile '{duplicate.Key.Id}' is declared more than once for sensor '{duplicate.Key.Sensor}'.");
        this.profiles = validated;
        AllowInlineCommands = allowInlineCommands;
    }

    /// <summary>The embedded defaults: what this repository shipped as sensor commands before S0.</summary>
    public static AnalyzerProfileCatalog BuiltIn { get; } = new(
    [
        new AnalyzerProfile(
            "eslint-frontend-sarif",
            "eslint",
            "node frontend/node_modules/eslint/bin/eslint.js . " +
            "--config frontend/eslint.config.mjs " +
            "--format frontend/node_modules/@microsoft/eslint-formatter-sarif/sarif.js " +
            "--output-file {reportPath}",
            WorkingDirectory: ".",
            ReportPath: ".quality/preflight/eslint.sarif",
            Description: "ESLint for a repository whose Node workspace lives in frontend/."),
        new AnalyzerProfile(
            "eslint-root-sarif",
            "eslint",
            "node node_modules/eslint/bin/eslint.js . " +
            "--config eslint.config.mjs " +
            "--format node_modules/@microsoft/eslint-formatter-sarif/sarif.js " +
            "--output-file {reportPath}",
            WorkingDirectory: ".",
            ReportPath: ".quality/preflight/eslint.sarif",
            Description: "ESLint for a repository whose Node workspace is the repository root."),
        new AnalyzerProfile(
            "roslyn-build-sarif",
            "roslyn",
            "dotnet build {target} --nologo \"-p:ErrorLog={reportPath},version=2.1\"",
            WorkingDirectory: ".",
            ReportPath: ".quality/preflight/roslyn.sarif",
            Description: "Roslyn analyzer diagnostics from a build, written as SARIF 2.1."),
        new AnalyzerProfile(
            "tsc-noemit",
            "tsc",
            "npx --no-install tsc --noEmit --pretty false",
            ReportPath: ".quality/preflight/tsc.txt",
            Description: "TypeScript diagnostics for the tsconfig in the scanned directory."),
    ]);

    /// <summary>
    /// The host catalogue: the file at <paramref name="path"/> when it exists, otherwise
    /// <see cref="BuiltIn"/>. A host file replaces the defaults rather than extending them, so a
    /// deployment that names its own profiles cannot silently fall back to a shipped command.
    /// </summary>
    public static AnalyzerProfileCatalog Load(string? path, bool allowInlineCommands = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return allowInlineCommands
                ? new AnalyzerProfileCatalog(BuiltIn.profiles, allowInlineCommands: true)
                : BuiltIn;
        AnalyzerProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AnalyzerProfileDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Analyzer profile file could not be read: {path}", exception);
        }
        if (document?.Profiles is not { Count: > 0 })
            throw new InvalidOperationException($"Analyzer profile file declares no profiles: {path}");
        return new AnalyzerProfileCatalog(document.Profiles, allowInlineCommands);
    }

    /// <summary>
    /// Whether repository configuration may still carry a raw <c>command</c>. False by default; a host
    /// that turns it on takes back the pre-S0 behaviour and must trust every registrar.
    /// </summary>
    public bool AllowInlineCommands { get; }

    public IReadOnlyList<AnalyzerProfile> ForSensor(string sensorId) => profiles
        .Where(profile => string.Equals(profile.Sensor, sensorId, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public bool TryResolve(string sensorId, string? profileId, out AnalyzerProfile profile)
    {
        profile = profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Sensor, sensorId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    private static AnalyzerProfile Validate(AnalyzerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Sensor))
            throw new InvalidOperationException("Every analyzer profile requires an id and a sensor.");
        // Parsing here means a malformed command is a host start-up error, never a run-time surprise.
        AnalyzerCommand.Split(profile.Command);
        return profile with { Id = profile.Id.Trim(), Sensor = profile.Sensor.Trim().ToLowerInvariant() };
    }

    private static IEqualityComparer<(string Sensor, string Id)> SensorProfileComparer { get; } =
        EqualityComparer<(string Sensor, string Id)>.Create(
            (left, right) =>
                StringComparer.OrdinalIgnoreCase.Equals(left.Sensor, right.Sensor) &&
                StringComparer.OrdinalIgnoreCase.Equals(left.Id, right.Id),
            value => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Sensor),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Id)));

    private sealed record AnalyzerProfileDocument(
        [property: JsonPropertyName("profiles")] IReadOnlyList<AnalyzerProfile>? Profiles);
}

/// <summary>What one sensor run is actually allowed to execute, after profile resolution.</summary>
public readonly record struct AnalyzerInvocation(string? Command, string? WorkingDirectory, string? ReportPath)
{
    /// <summary>
    /// Resolves repository sensor configuration into an invocation. A <c>profile</c> id selects a
    /// host-owned command; <c>reportPath</c> and <c>workingDirectory</c> may still be redirected by the
    /// repository because both are confined to it. A raw <c>command</c> is refused unless the host
    /// deliberately re-enabled inline commands.
    /// </summary>
    public static bool TryResolve(
        string sensorId,
        IReadOnlyDictionary<string, string> configuration,
        AnalyzerProfileCatalog profiles,
        out AnalyzerInvocation invocation,
        out string refusal)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(profiles);
        invocation = default;
        refusal = string.Empty;
        string? command = null;
        string? workingDirectory = null;
        string? reportPath = null;

        if (configuration.TryGetValue(AnalyzerSensorConfiguration.ProfileKey, out var profileId) &&
            !string.IsNullOrWhiteSpace(profileId))
        {
            if (!profiles.TryResolve(sensorId, profileId, out var profile))
            {
                var known = profiles.ForSensor(sensorId).Select(candidate => candidate.Id).ToArray();
                refusal = $"Analyzer profile '{profileId}' is not configured for sensor '{sensorId}'. " +
                          (known.Length == 0
                              ? "This host offers no profile for that sensor."
                              : $"This host offers: {string.Join(", ", known)}.");
                return false;
            }
            command = profile.Command;
            workingDirectory = profile.WorkingDirectory;
            reportPath = profile.ReportPath;
        }

        if (configuration.TryGetValue(AnalyzerSensorConfiguration.CommandKey, out var inline) &&
            !string.IsNullOrWhiteSpace(inline))
        {
            if (!profiles.AllowInlineCommands)
            {
                refusal = $"Repository configuration for sensor '{sensorId}' carries an executable command. " +
                          "Analyzer commands are host-owned; select a profile id instead.";
                return false;
            }
            command = inline;
        }

        if (configuration.TryGetValue("reportPath", out var configuredReport) &&
            !string.IsNullOrWhiteSpace(configuredReport))
            reportPath = configuredReport;
        if (configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
            !string.IsNullOrWhiteSpace(configuredWorkingDirectory))
            workingDirectory = configuredWorkingDirectory;

        invocation = new AnalyzerInvocation(command, workingDirectory, reportPath);
        return true;
    }
}

/// <summary>
/// The configuration keys each sensor understands. Everything a repository registration carries is
/// checked against this allowlist, so an unknown or executable key is refused at the API boundary
/// instead of being handed to a sensor.
/// </summary>
public static class AnalyzerSensorConfiguration
{
    /// <summary>The key that used to carry a free-form host command. Refused unless the host opts back in.</summary>
    public const string CommandKey = "command";

    /// <summary>The key that selects a host-owned <see cref="AnalyzerProfile"/>.</summary>
    public const string ProfileKey = "profile";

    private static readonly IReadOnlyDictionary<string, string[]> Keys =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sarif"] = [ProfileKey, "reportPath", "workingDirectory"],
            ["roslyn"] = [ProfileKey, "reportPath", "workingDirectory"],
            ["eslint"] = [ProfileKey, "reportPath", "workingDirectory"],
            ["tsc"] = [ProfileKey, "reportPath", "workingDirectory", "producerVersion"],
            ["coverage"] = ["reportPaths"],
            ["dependencies"] = ["ecosystems"],
            ["dotnet-build"] = ["target"],
            ["gitleaks"] = ["mode", "range", "configPath", "baselinePath"],
            ["angular-compiler"] = [],
            ["boundaries"] = [],
        };

    /// <summary>
    /// The keys <paramref name="sensorId"/> accepts, or null for a sensor this table does not know.
    /// A null answer suppresses only the allowlist check; <see cref="CommandKey"/> stays refused.
    /// </summary>
    public static IReadOnlyList<string>? AllowedKeys(string sensorId) =>
        Keys.TryGetValue(sensorId, out var keys) ? keys : null;
}
