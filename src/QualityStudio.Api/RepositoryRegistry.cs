using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record RepositoryRegistration(
    string Id,
    string DisplayName,
    string RootPath,
    string? GlobalInputsDirectory,
    int InputBudgetCharacters,
    IReadOnlyList<string> EnabledReviewKinds,
    IReadOnlyList<RepositorySensorConfiguration>? Sensors = null,
    bool Archived = false,
    long? DefaultReviewTokenCap = null,
    decimal? DefaultReviewCostCap = null);

public sealed record RepositoryRegistrationRequest(
    string? Id,
    string DisplayName,
    string RootPath,
    string? GlobalInputsDirectory,
    int? InputBudgetCharacters,
    IReadOnlyList<string>? EnabledReviewKinds,
    IReadOnlyList<RepositorySensorConfiguration>? Sensors = null,
    long? DefaultReviewTokenCap = null,
    decimal? DefaultReviewCostCap = null);

public sealed record RepositorySensorConfiguration(
    string Id,
    bool Enabled = true,
    IReadOnlyDictionary<string, string>? Configuration = null,
    string? ProfileId = null);

public sealed class RepositoryRegistry
{
    public const string DefaultRepositoryId = "default";
    public const string RelativeRegistryPath = ".quality-studio/repositories.json";
    private static readonly string[] SupportedKinds = ["code", "security", "performance"];
    private static readonly HashSet<string> CommandBackedSensors =
        new(["sarif", "roslyn", "eslint", "tsc"], StringComparer.Ordinal);
    private readonly string registryPath;
    private readonly string contentRoot;
    private readonly RepositoryOptions legacyOptions;
    private readonly string[] allowedRoots;
    private readonly IReadOnlyList<string> supportedSensors;
    private readonly IReadOnlyDictionary<string, AnalyzerProfile> analyzerProfiles;
    private readonly ILogger<RepositoryRegistry> logger;
    private readonly ReviewMetaIndex metaIndex;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<RepositoryRegistration> entries;

    public RepositoryRegistry(IHostEnvironment environment, IOptions<RepositoryOptions> options,
        SensorRegistry sensors, ILogger<RepositoryRegistry> logger, ReviewMetaIndex metaIndex)
    {
        contentRoot = environment.ContentRootPath;
        legacyOptions = options.Value;
        supportedSensors = sensors.List().Select(sensor => sensor.Id).ToArray();
        analyzerProfiles = ValidateAnalyzerProfiles(legacyOptions.AnalyzerProfiles);
        this.logger = logger;
        this.metaIndex = metaIndex;
        if (legacyOptions.AllowedRoots.Length == 0)
            throw new InvalidOperationException("QualityStudio:AllowedRoots must contain at least one directory.");
        allowedRoots = legacyOptions.AllowedRoots.Select(path => ResolvePath(path, contentRoot))
            .Distinct(PathComparer).ToArray();
        foreach (var allowedRoot in allowedRoots)
        {
            if (!Directory.Exists(allowedRoot))
                throw new InvalidOperationException("A configured repository allowed root does not exist.");
            PathConfinement.RejectReparseTraversal(allowedRoot, allowedRoot);
        }
        registryPath = Path.Combine(contentRoot, RelativeRegistryPath.Replace('/', Path.DirectorySeparatorChar));
        entries = LoadOrSeed();
    }

    public string RegistryPath => registryPath;

    public IReadOnlyList<RepositoryRegistration> List(bool includeArchived = false) => entries
        .Where(entry => includeArchived || !entry.Archived)
        .OrderBy(entry => entry.Id == DefaultRepositoryId ? 0 : 1)
        .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public RepositoryRegistration Get(string? id, bool includeArchived = false)
    {
        var resolvedId = string.IsNullOrWhiteSpace(id) ? DefaultRepositoryId : id;
        return entries.FirstOrDefault(entry =>
                   string.Equals(entry.Id, resolvedId, StringComparison.OrdinalIgnoreCase) &&
                   (includeArchived || !entry.Archived))
               ?? throw new KeyNotFoundException($"Repository '{resolvedId}' was not found.");
    }

    public RepositoryAccess Access(string? id) => new(Get(id).RootPath, metaIndex);

    public async Task<RepositoryRegistration> CreateAsync(RepositoryRegistrationRequest request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = Validate(request, null);
            if (entries.Any(existing => string.Equals(existing.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RepositoryRegistryValidationException($"A repository with id '{entry.Id}' already exists.");
            }

            entries.Add(entry);
            await PersistAsync(cancellationToken);
            logger.LogInformation(new EventId(1400, "RepositoryOnboarded"),
                "Onboarded repository {RepositoryId} at {RepositoryRoot}", entry.Id, entry.RootPath);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RepositoryRegistration> UpdateAsync(string id, RepositoryRegistrationRequest request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = Get(id, includeArchived: true);
            if (existing.Archived)
            {
                throw new RepositoryRegistryValidationException("Archived repositories cannot be edited.");
            }

            var updated = Validate(request with
            {
                Id = existing.Id,
                Sensors = request.Sensors ?? ForTrustedUpdate(existing.Sensors),
            }, existing.Id);
            entries[entries.IndexOf(existing)] = updated;
            await PersistAsync(cancellationToken);
            logger.LogInformation(new EventId(1401, "RepositoryUpdated"),
                "Updated repository {RepositoryId} at {RepositoryRoot}", updated.Id, updated.RootPath);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RepositoryRegistration> ArchiveAsync(string id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = Get(id, includeArchived: true);
            if (existing.Archived)
            {
                return existing;
            }

            if (string.Equals(existing.Id, DefaultRepositoryId, StringComparison.OrdinalIgnoreCase))
            {
                throw new RepositoryRegistryValidationException("The default repository cannot be archived because legacy API routes depend on it.");
            }

            if (entries.Count(entry => !entry.Archived) <= 1)
            {
                throw new RepositoryRegistryValidationException("The last active repository cannot be archived.");
            }

            var archived = existing with { Archived = true };
            entries[entries.IndexOf(existing)] = archived;
            await PersistAsync(cancellationToken);
            logger.LogInformation(new EventId(1402, "RepositoryArchived"), "Archived repository {RepositoryId}", id);
            return archived;
        }
        finally
        {
            gate.Release();
        }
    }

    private List<RepositoryRegistration> LoadOrSeed()
    {
        if (File.Exists(registryPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<RepositoryRegistration>>(File.ReadAllText(registryPath), JsonOptions());
                if (loaded is { Count: > 0 })
                {
                    var migrated = loaded.Select(entry => entry with
                    {
                        Sensors = MergeSupportedSensors(entry.Sensors),
                    }).ToList();
                    foreach (var entry in migrated) ValidatePersistedEntry(entry);
                    return migrated;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                throw new InvalidOperationException($"Repository registry could not be read: {registryPath}", exception);
            }
        }

        var root = ResolvePath(legacyOptions.RepositoryRoot, contentRoot);
        EnsureAllowedDirectory(root, "Configured repository root is outside the allowed roots.");
        var displayName = new DirectoryInfo(root).Name;
        var seeded = new RepositoryRegistration(
            DefaultRepositoryId,
            string.IsNullOrWhiteSpace(displayName) ? "Default repository" : displayName,
            root,
            ValidateOptionalDirectory(legacyOptions.GlobalInputsDirectory, root),
            legacyOptions.InputBudgetCharacters,
            SupportedKinds,
            DefaultSensors(),
            DefaultReviewTokenCap: legacyOptions.DefaultReviewTokenCap);
        var result = new List<RepositoryRegistration> { seeded };
        entries = result;
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(registryPath, JsonSerializer.Serialize(result, JsonOptions()));
        logger.LogInformation(new EventId(1403, "RepositoryRegistrySeeded"),
            "Seeded repository registry {RegistryPath} from legacy root {RepositoryRoot}", registryPath, root);
        return result;
    }

    private RepositoryRegistration Validate(RepositoryRegistrationRequest request, string? existingId)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new RepositoryRegistryValidationException("Display name is required.");
        }

        var id = existingId ?? Slugify(string.IsNullOrWhiteSpace(request.Id) ? request.DisplayName : request.Id);
        if (id.Length is < 1 or > 64 || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new RepositoryRegistryValidationException("Repository id must contain only lowercase letters, numbers, and hyphens.");
        }

        id = id.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            throw new RepositoryRegistryValidationException("Repository path is required.");
        }

        var root = ResolvePath(request.RootPath, contentRoot);
        if (!Directory.Exists(root))
        {
            throw new RepositoryRegistryValidationException(
                $"Repository path does not exist or is not a directory: {root}",
                "Repository path does not exist");
        }

        EnsureAllowedDirectory(root, "Repository path is outside the configured allowed roots.");

        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            throw new RepositoryRegistryValidationException($"Path is not a Git repository: {root}",
                "Repository path is not a Git repository");
        }

        var budget = request.InputBudgetCharacters ?? InputResolver.DefaultBudgetCharacters;
        if (budget is < 1000 or > 1_000_000)
        {
            throw new RepositoryRegistryValidationException("Input budget must be between 1,000 and 1,000,000 characters.");
        }

        var kinds = (request.EnabledReviewKinds ?? SupportedKinds)
            .Select(kind => kind.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (kinds.Length == 0 || kinds.Any(kind => !SupportedKinds.Contains(kind, StringComparer.Ordinal)))
        {
            throw new RepositoryRegistryValidationException("Select at least one supported review kind: code, security, or performance.");
        }

        var requestedSensors = request.Sensors ?? DefaultSensors();
        if (requestedSensors.Any(sensor => string.IsNullOrWhiteSpace(sensor.Id)))
        {
            throw new RepositoryRegistryValidationException("Every sensor configuration requires an id.");
        }

        var sensors = requestedSensors
            .Select(sensor => sensor with
            {
                Id = sensor.Id.Trim().ToLowerInvariant(),
                Configuration = sensor.Configuration is null
                    ? null
                    : new Dictionary<string, string>(sensor.Configuration, StringComparer.Ordinal),
            })
            .ToArray();
        if (sensors.Length == 0 ||
            sensors.Any(sensor => !supportedSensors.Contains(sensor.Id, StringComparer.Ordinal)) ||
            sensors.Select(sensor => sensor.Id).Distinct(StringComparer.Ordinal).Count() != sensors.Length)
        {
            throw new RepositoryRegistryValidationException(
                $"Sensors must be a unique selection of: {string.Join(", ", supportedSensors)}.");
        }
        sensors = sensors.Select(ResolveRequestedSensor).ToArray();

        if (request.DefaultReviewTokenCap.HasValue && request.DefaultReviewCostCap.HasValue)
            throw new RepositoryRegistryValidationException("Choose either a default token cap or a default cost cap, not both.");
        if (request.DefaultReviewTokenCap is <= 0 or > 1_000_000_000)
            throw new RepositoryRegistryValidationException("Default review token cap must be between 1 and 1,000,000,000 tokens.");
        if (request.DefaultReviewCostCap is <= 0 or > 1_000_000)
            throw new RepositoryRegistryValidationException("Default review cost cap must be between 0 and 1,000,000.");

        return new RepositoryRegistration(id, request.DisplayName.Trim(), root,
            ValidateOptionalDirectory(request.GlobalInputsDirectory, root), budget, kinds, sensors,
            DefaultReviewTokenCap: request.DefaultReviewTokenCap,
            DefaultReviewCostCap: request.DefaultReviewCostCap);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        var temporaryPath = registryPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions()), cancellationToken);
        File.Move(temporaryPath, registryPath, true);
    }

    private static string ResolvePath(string path, string relativeTo) => Path.GetFullPath(
        Path.IsPathRooted(path) ? path : Path.Combine(relativeTo, path));

    private string? ValidateOptionalDirectory(string? path, string relativeTo)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var resolved = ResolvePath(path, relativeTo);
        if (!Directory.Exists(resolved))
            throw new RepositoryRegistryValidationException(
                $"Global inputs directory does not exist or is not a directory: {resolved}",
                "Global inputs directory does not exist");
        EnsureAllowedDirectory(resolved, "Global inputs directory is outside the configured allowed roots.");
        return resolved;
    }

    private void ValidatePersistedEntry(RepositoryRegistration entry)
    {
        if (!Directory.Exists(entry.RootPath))
            throw new InvalidOperationException("A registered repository is unavailable.");
        EnsureAllowedDirectory(entry.RootPath, "A registered repository is outside the configured allowed roots.");
        if (entry.GlobalInputsDirectory is not null)
        {
            if (!Directory.Exists(entry.GlobalInputsDirectory))
                throw new InvalidOperationException("A registered global inputs directory is unavailable.");
            EnsureAllowedDirectory(entry.GlobalInputsDirectory,
                "A registered global inputs directory is outside the configured allowed roots.");
        }
    }

    private void EnsureAllowedDirectory(string path, string internalMessage)
    {
        var allowedRoot = allowedRoots.FirstOrDefault(root => PathConfinement.IsWithin(root, path));
        if (allowedRoot is null)
            throw new RepositoryRegistryValidationException(internalMessage,
                internalMessage.Contains("inputs", StringComparison.OrdinalIgnoreCase)
                    ? "Global inputs directory is outside the allowed roots"
                    : "Repository path is outside the allowed roots");
        try
        {
            PathConfinement.RejectReparseTraversal(allowedRoot, path);
        }
        catch (ArgumentException exception)
        {
            throw new RepositoryRegistryValidationException(internalMessage,
                "Configured path traverses a symbolic link or junction", exception);
        }
    }

    private static string Slugify(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private IReadOnlyList<RepositorySensorConfiguration> DefaultSensors() =>
        supportedSensors.Select(id => new RepositorySensorConfiguration(
            id,
            Enabled: !CommandBackedSensors.Contains(id))).ToArray();

    private IReadOnlyList<RepositorySensorConfiguration> MergeSupportedSensors(
        IReadOnlyList<RepositorySensorConfiguration>? configured)
    {
        var existing = (configured ?? Array.Empty<RepositorySensorConfiguration>())
            .ToDictionary(sensor => sensor.Id, StringComparer.OrdinalIgnoreCase);
        return supportedSensors
            .Select(id => existing.TryGetValue(id, out var sensor)
                ? ResolvePersistedSensor(sensor with { Id = id })
                : new RepositorySensorConfiguration(id, Enabled: !CommandBackedSensors.Contains(id)))
            .ToArray();
    }

    private RepositorySensorConfiguration ResolveRequestedSensor(RepositorySensorConfiguration sensor)
    {
        if (!CommandBackedSensors.Contains(sensor.Id))
        {
            if (!string.IsNullOrWhiteSpace(sensor.ProfileId))
                throw new RepositoryRegistryValidationException(
                    $"Sensor '{sensor.Id}' does not accept an analyzer profile.");
            return sensor;
        }

        if (sensor.Configuration is { Count: > 0 })
            throw new RepositoryRegistryValidationException(
                $"Sensor '{sensor.Id}' rejects repository-provided commands; select a host-owned profileId.");
        if (string.IsNullOrWhiteSpace(sensor.ProfileId))
        {
            if (sensor.Enabled)
                throw new RepositoryRegistryValidationException(
                    $"Command-backed sensor '{sensor.Id}' is disabled unless a host-owned profileId is selected.");
            return sensor with { Configuration = null, ProfileId = null };
        }
        return ApplyProfile(sensor, sensor.ProfileId);
    }

    private RepositorySensorConfiguration ResolvePersistedSensor(RepositorySensorConfiguration sensor)
    {
        if (!CommandBackedSensors.Contains(sensor.Id)) return sensor;
        if (string.IsNullOrWhiteSpace(sensor.ProfileId) || !analyzerProfiles.ContainsKey(sensor.ProfileId))
        {
            if (sensor.Enabled || sensor.Configuration is { Count: > 0 })
            {
                logger.LogWarning(new EventId(1404, "UnsafeAnalyzerConfigurationDisabled"),
                    "Disabled legacy command-backed sensor {SensorId}; select a host-owned analyzer profile", sensor.Id);
            }
            return sensor with { Enabled = false, Configuration = null, ProfileId = null };
        }
        return ApplyProfile(sensor, sensor.ProfileId);
    }

    private RepositorySensorConfiguration ApplyProfile(RepositorySensorConfiguration sensor, string profileId)
    {
        var normalizedProfileId = profileId.Trim().ToLowerInvariant();
        if (!analyzerProfiles.TryGetValue(normalizedProfileId, out var profile) ||
            !string.Equals(profile.SensorId, sensor.Id, StringComparison.Ordinal))
            throw new RepositoryRegistryValidationException(
                $"Analyzer profile '{normalizedProfileId}' is not available for sensor '{sensor.Id}'.");
        return sensor with
        {
            ProfileId = normalizedProfileId,
            Configuration = profile.Configuration,
        };
    }

    private IReadOnlyDictionary<string, AnalyzerProfile> ValidateAnalyzerProfiles(
        IReadOnlyDictionary<string, AnalyzerProfileOptions>? configured)
    {
        var profiles = new Dictionary<string, AnalyzerProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (configuredId, options) in configured ??
                 new Dictionary<string, AnalyzerProfileOptions>(StringComparer.OrdinalIgnoreCase))
        {
            var id = configuredId.Trim().ToLowerInvariant();
            var sensorId = options.SensorId.Trim().ToLowerInvariant();
            if (id.Length is < 1 or > 64 || id.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
                throw new InvalidOperationException("Analyzer profile ids may contain only letters, numbers, '-' and '_'.");
            if (!CommandBackedSensors.Contains(sensorId) || !supportedSensors.Contains(sensorId, StringComparer.Ordinal))
                throw new InvalidOperationException($"Analyzer profile '{id}' names an unsupported command-backed sensor.");
            if (string.IsNullOrWhiteSpace(options.Executable))
                throw new InvalidOperationException($"Analyzer profile '{id}' requires an executable.");
            if (!IsConfinedRelativePath(options.ReportPath))
                throw new InvalidOperationException($"Analyzer profile '{id}' requires a repository-relative report path.");
            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory) &&
                !IsConfinedRelativePath(options.WorkingDirectory))
                throw new InvalidOperationException(
                    $"Analyzer profile '{id}' requires a repository-relative working directory.");

            var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = string.Join(' ', new[] { options.Executable }
                    .Concat(options.Arguments ?? []).Select(QuoteCommandToken)),
                ["reportPath"] = options.ReportPath,
            };
            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
                configuration["workingDirectory"] = options.WorkingDirectory;
            if (!string.IsNullOrWhiteSpace(options.ProducerVersion))
                configuration["producerVersion"] = options.ProducerVersion;
            if (!profiles.TryAdd(id, new AnalyzerProfile(sensorId, configuration)))
                throw new InvalidOperationException($"Analyzer profile id '{id}' is duplicated.");
        }
        return profiles;
    }

    private static IReadOnlyList<RepositorySensorConfiguration>? ForTrustedUpdate(
        IReadOnlyList<RepositorySensorConfiguration>? sensors) => sensors?
        .Select(sensor => CommandBackedSensors.Contains(sensor.Id)
            ? sensor with { Configuration = null }
            : sensor)
        .ToArray();

    private static string QuoteCommandToken(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(character => char.IsWhiteSpace(character) || character is '\"' or '\\')) return value;
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsConfinedRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");

    private sealed record AnalyzerProfile(string SensorId, IReadOnlyDictionary<string, string> Configuration);
}

public sealed class RepositoryRegistryValidationException : Exception
{
    public RepositoryRegistryValidationException(string message, string publicTitle = "Invalid repository configuration",
        Exception? innerException = null) : base(message, innerException) => PublicTitle = publicTitle;

    public string PublicTitle { get; }
}
