using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed class AnalyzerExecutionPolicy
{
    public const string ProfileIdKey = "profileId";
    private static readonly HashSet<string> ProcessBackedSensors = new(
        ["dependencies", "eslint", "gitleaks", "roslyn", "sarif", "tsc"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProfileSensors = new(
        ["eslint", "roslyn", "sarif", "tsc"],
        StringComparer.OrdinalIgnoreCase);
    private readonly bool executableSensorsEnabled;
    private readonly IReadOnlyDictionary<string, AnalyzerProfileOptions> profiles;

    public AnalyzerExecutionPolicy(IOptions<RepositoryOptions> configured)
    {
        executableSensorsEnabled = configured.Value.Security.ExecutableSensorsEnabled;
        profiles = ValidateProfiles(configured.Value.AnalyzerProfiles);
    }

    public bool CanProbe(IReviewSensor sensor) =>
        executableSensorsEnabled || !ProcessBackedSensors.Contains(sensor.Id);

    public bool CanExecute(RepositorySensorConfiguration sensor) =>
        executableSensorsEnabled || !RequiresProcess(sensor);

    public void EnsureCanExecute(RepositorySensorConfiguration sensor)
    {
        if (!CanExecute(sensor))
        {
            throw new ExecutableSensorDisabledException(
                $"Executable sensor '{sensor.Id}' is disabled until isolated workers are configured.");
        }
    }

    public void ValidateRepositoryConfiguration(RepositorySensorConfiguration sensor)
    {
        var configuration = sensor.Configuration;
        if (configuration is null) return;
        if (configuration.Keys.Any(key => string.Equals(key, "command", StringComparison.OrdinalIgnoreCase)))
        {
            throw new RepositoryRegistryValidationException(
                "Free-form analyzer commands are not accepted. Select a host-owned analyzer profile.",
                "Free-form analyzer commands are not permitted");
        }

        var containsProfileId = configuration.Keys.Any(key =>
            string.Equals(key, ProfileIdKey, StringComparison.OrdinalIgnoreCase));
        if (!TryProfileId(configuration, out var profileId))
        {
            if (containsProfileId)
                throw new RepositoryRegistryValidationException("Analyzer profile id cannot be empty.");
            return;
        }
        if (!ProfileSensors.Contains(sensor.Id))
        {
            throw new RepositoryRegistryValidationException(
                $"Sensor '{sensor.Id}' does not accept analyzer profiles.");
        }
        if (configuration.Count != 1)
        {
            throw new RepositoryRegistryValidationException(
                "Repository analyzer configuration may select only a host-owned profile id.");
        }
        if (!profiles.TryGetValue(profileId, out var profile) ||
            !string.Equals(profile.SensorId, sensor.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositoryRegistryValidationException(
                $"Analyzer profile '{profileId}' is not available for sensor '{sensor.Id}'.");
        }
    }

    public IReadOnlyDictionary<string, string>? ResolveConfiguration(RepositorySensorConfiguration sensor)
    {
        ValidateRepositoryConfiguration(sensor);
        if (sensor.Configuration is null || !TryProfileId(sensor.Configuration, out var profileId))
            return sensor.Configuration;

        EnsureCanExecute(sensor);
        var profile = profiles[profileId];
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = string.Join(' ', new[] { profile.Executable }.Concat(profile.Arguments).Select(QuoteToken)),
            ["reportPath"] = profile.ReportPath,
        };
        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
            configuration["workingDirectory"] = profile.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(profile.ProducerVersion))
            configuration["producerVersion"] = profile.ProducerVersion;
        return configuration;
    }

    public static SensorAvailability DisabledAvailability(string sensorId) => new(
        false,
        $"Executable sensor '{sensorId}' is disabled until isolated workers are configured.");

    private static bool RequiresProcess(RepositorySensorConfiguration sensor)
    {
        if (!ProcessBackedSensors.Contains(sensor.Id)) return false;
        if (sensor.Id is "sarif" or "roslyn" or "eslint")
            return sensor.Configuration is not null && TryProfileId(sensor.Configuration, out _);
        return true;
    }

    private static bool TryProfileId(IReadOnlyDictionary<string, string> configuration, out string profileId)
    {
        var value = configuration.FirstOrDefault(pair =>
            string.Equals(pair.Key, ProfileIdKey, StringComparison.OrdinalIgnoreCase)).Value;
        profileId = value?.Trim() ?? string.Empty;
        return profileId.Length > 0;
    }

    private static IReadOnlyDictionary<string, AnalyzerProfileOptions> ValidateProfiles(
        IReadOnlyList<AnalyzerProfileOptions> configured)
    {
        var result = new Dictionary<string, AnalyzerProfileOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in configured)
        {
            var id = profile.Id.Trim();
            var sensorId = profile.SensorId.Trim().ToLowerInvariant();
            if (id.Length is < 1 or > 128 || !result.TryAdd(id, profile))
                throw new InvalidOperationException("Analyzer profile ids must be non-empty and unique.");
            if (!ProfileSensors.Contains(sensorId))
                throw new InvalidOperationException("Analyzer profiles must target sarif, roslyn, eslint, or tsc.");
            if (string.IsNullOrWhiteSpace(profile.Executable) || HasControlCharacter(profile.Executable) ||
                profile.Arguments.Any(argument => HasControlCharacter(argument)))
                throw new InvalidOperationException("Analyzer profiles require a valid executable and argument template.");
            ValidateRelativePath(profile.ReportPath, "report path");
            if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
                ValidateRelativePath(profile.WorkingDirectory, "working directory");
            profile.Id = id;
            profile.SensorId = sensorId;
        }
        return result;
    }

    private static void ValidateRelativePath(string path, string name)
    {
        var trimmed = path.Trim();
        var hasWindowsDrive = trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':';
        if (trimmed.Length == 0 || trimmed != path || HasControlCharacter(path) ||
            Path.IsPathRooted(path) || hasWindowsDrive || trimmed.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Analyzer profile {name} must be a repository-relative path.");
        }
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
            throw new InvalidOperationException($"Analyzer profile {name} cannot traverse outside the repository.");
    }

    private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

    private static string QuoteToken(string token) =>
        $"\"{token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

public sealed class ExecutableSensorDisabledException(string message) : Exception(message);
