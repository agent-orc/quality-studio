using System.Text.Json;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record AnalyzerExecutionProfile(
    string Id,
    string SensorId,
    string Executable,
    IReadOnlyList<string> Arguments,
    string ReportPath,
    string? WorkingDirectory,
    string? ProducerVersion);

/// <summary>
/// Resolves API-selectable profile ids to immutable, host-owned analyzer execution settings.
/// Repository registration data never carries an executable or argument template.
/// </summary>
public sealed class AnalyzerProfileRegistry
{
    private static readonly HashSet<string> CommandBackedSensorIds =
        ["sarif", "roslyn", "eslint", "tsc"];
    private static readonly HashSet<string> ForbiddenRepositoryKeys = new(StringComparer.OrdinalIgnoreCase)
        { "command", "executable", "arguments", "profileExecutable", "profileArguments" };
    private readonly IReadOnlyDictionary<string, AnalyzerExecutionProfile> profiles;

    public AnalyzerProfileRegistry(IOptions<RepositoryOptions> configured)
    {
        CommandBackedAnalyzersEnabled = configured.Value.Security.CommandBackedAnalyzersEnabled;
        var validated = new Dictionary<string, AnalyzerExecutionProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in configured.Value.AnalyzerProfiles)
        {
            var id = option.Id.Trim().ToLowerInvariant();
            var sensorId = option.SensorId.Trim().ToLowerInvariant();
            if (id.Length is < 1 or > 64 || id.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
                throw new InvalidOperationException("Analyzer profile ids may contain only lowercase letters, numbers, dots, and hyphens.");
            if (!CommandBackedSensorIds.Contains(sensorId))
                throw new InvalidOperationException($"Analyzer profile '{id}' names an unsupported command-backed sensor.");
            if (!validated.TryAdd(id, Validate(option, id, sensorId)))
                throw new InvalidOperationException($"Analyzer profile id '{id}' is configured more than once.");
        }
        profiles = validated;
    }

    public bool CommandBackedAnalyzersEnabled { get; }

    public static bool IsCommandBacked(string sensorId) => CommandBackedSensorIds.Contains(sensorId);

    public static bool ContainsForbiddenRepositoryConfiguration(
        IReadOnlyDictionary<string, string>? configuration) =>
        configuration?.Keys.Any(ForbiddenRepositoryKeys.Contains) == true;

    public bool HasProfile(string profileId, string sensorId) =>
        profiles.TryGetValue(profileId, out var profile) &&
        string.Equals(profile.SensorId, sensorId, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string>? Resolve(RepositorySensorConfiguration configuration)
    {
        if (!IsCommandBacked(configuration.Id)) return configuration.Configuration;
        if (!configuration.Enabled) return null;
        if (!CommandBackedAnalyzersEnabled)
            throw new RepositoryRegistryValidationException("Command-backed analyzers are disabled until isolated workers are enabled.");
        if (string.IsNullOrWhiteSpace(configuration.ProfileId) ||
            !profiles.TryGetValue(configuration.ProfileId, out var profile) ||
            !string.Equals(profile.SensorId, configuration.Id, StringComparison.OrdinalIgnoreCase))
            throw new RepositoryRegistryValidationException(
                $"Sensor '{configuration.Id}' requires a matching host-owned analyzer profile id.");

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileId"] = profile.Id,
            ["profileExecutable"] = profile.Executable,
            ["profileArguments"] = JsonSerializer.Serialize(profile.Arguments),
            ["reportPath"] = profile.ReportPath,
        };
        if (profile.WorkingDirectory is not null) resolved["workingDirectory"] = profile.WorkingDirectory;
        if (profile.ProducerVersion is not null) resolved["producerVersion"] = profile.ProducerVersion;
        return resolved;
    }

    private static AnalyzerExecutionProfile Validate(AnalyzerProfileOptions option, string id, string sensorId)
    {
        var executable = option.Executable.Trim();
        if (executable.Length is < 1 or > 1024 || executable.Any(char.IsWhiteSpace) || executable.Contains('{'))
            throw new InvalidOperationException($"Analyzer profile '{id}' requires one fixed executable without placeholders.");
        if (option.Arguments.Length > 128 || option.Arguments.Any(argument => argument is null || argument.Length > 4096))
            throw new InvalidOperationException($"Analyzer profile '{id}' has an invalid argument template.");
        var reportPath = option.ReportPath.Trim();
        if (reportPath.Length is < 1 or > 1024 || !IsContainedRelativePath(reportPath))
            throw new InvalidOperationException($"Analyzer profile '{id}' requires a repository-relative report path.");
        var workingDirectory = string.IsNullOrWhiteSpace(option.WorkingDirectory)
            ? null
            : option.WorkingDirectory.Trim();
        if (workingDirectory is not null && !IsContainedRelativePath(workingDirectory))
            throw new InvalidOperationException($"Analyzer profile '{id}' working directory must be repository-relative.");
        var producerVersion = string.IsNullOrWhiteSpace(option.ProducerVersion)
            ? null
            : option.ProducerVersion.Trim();
        return new AnalyzerExecutionProfile(
            id, sensorId, executable, option.Arguments.ToArray(), reportPath, workingDirectory, producerVersion);
    }

    private static bool IsContainedRelativePath(string path) =>
        !Path.IsPathFullyQualified(path) &&
        !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal);
}
