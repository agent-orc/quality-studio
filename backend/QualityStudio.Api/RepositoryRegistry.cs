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
    decimal? DefaultReviewCostCap = null,
    IReadOnlyList<RepositoryExternalRef>? ExternalRefs = null);

public sealed record RepositoryRegistrationRequest(
    string? Id,
    string DisplayName,
    string RootPath,
    string? GlobalInputsDirectory,
    int? InputBudgetCharacters,
    IReadOnlyList<string>? EnabledReviewKinds,
    IReadOnlyList<RepositorySensorConfiguration>? Sensors = null,
    long? DefaultReviewTokenCap = null,
    decimal? DefaultReviewCostCap = null,
    IReadOnlyList<RepositoryExternalRef>? ExternalRefs = null);

/// <summary>
/// A stable reference to this repository in another system, for example the Agent Studio project
/// <c>agent-studio:PROJ-002</c>. It lets that system find its registration again without matching
/// paths. Unique per <c>system:id</c> across all registrations, archived ones included.
/// </summary>
public sealed record RepositoryExternalRef(string System, string Id, string? Url = null)
{
    public string Key => $"{System}:{Id}";

    /// <summary>Parses <c>system:id</c>; the id may itself contain colons.</summary>
    public static bool TryParse(string? value, out string system, out string id)
    {
        system = id = "";
        var separator = value?.IndexOf(':') ?? -1;
        if (value is null || separator <= 0 || separator == value.Length - 1) return false;
        system = value[..separator].Trim();
        id = value[(separator + 1)..].Trim();
        return system.Length > 0 && id.Length > 0;
    }
}

/// <summary>
/// A persisted registration this host cannot serve. It is kept in the registry file and reported, so a
/// moved or removed working copy is a visible fact instead of a host that refuses to start.
/// </summary>
public sealed record RepositoryUnavailability(
    string Id,
    string DisplayName,
    string RootPath,
    string Status,
    string Reason);

public sealed record RepositorySensorConfiguration(
    string Id,
    bool Enabled = true,
    IReadOnlyDictionary<string, string>? Configuration = null);

public sealed class RepositoryRegistry
{
    public const string DefaultRepositoryId = "default";
    public const string RelativeRegistryPath = ".quality-studio/repositories.json";
    private static readonly string[] SupportedKinds = ["code", "security", "performance"];
    private readonly string registryPath;
    private readonly string contentRoot;
    private readonly RepositoryOptions legacyOptions;
    private readonly string[] allowedRoots;
    private readonly IReadOnlyList<string> supportedSensors;
    private readonly ILogger<RepositoryRegistry> logger;
    private readonly ReviewMetaIndex metaIndex;
    private readonly AnalyzerProfileCatalog profiles;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// Every persisted registration, replaced as a whole under <see cref="gate"/>. Readers take the
    /// reference once and enumerate an immutable array, so a concurrent mutation can never be observed
    /// half applied.
    /// </summary>
    private volatile IReadOnlyList<RepositoryRegistration> entries = [];

    /// <summary>Ids this host cannot serve, by id. Written once at load and whenever a mutation heals one.</summary>
    private volatile IReadOnlyDictionary<string, RepositoryUnavailability> quarantine =
        new Dictionary<string, RepositoryUnavailability>(StringComparer.OrdinalIgnoreCase);

    public RepositoryRegistry(IHostEnvironment environment, IOptions<RepositoryOptions> options,
        SensorRegistry sensors, ILogger<RepositoryRegistry> logger, ReviewMetaIndex metaIndex,
        AnalyzerProfileCatalog profiles)
    {
        contentRoot = environment.ContentRootPath;
        legacyOptions = options.Value;
        supportedSensors = sensors.List().Select(sensor => sensor.Id).ToArray();
        this.logger = logger;
        this.metaIndex = metaIndex;
        this.profiles = profiles;
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

    public IReadOnlyList<RepositoryRegistration> List(bool includeArchived = false)
    {
        var snapshot = entries;
        var unavailable = quarantine;
        return snapshot
            .Where(entry => (includeArchived || !entry.Archived) && !unavailable.ContainsKey(entry.Id))
            .OrderBy(entry => entry.Id == DefaultRepositoryId ? 0 : 1)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The active registrations this host loaded but cannot serve, with the reason and the path. An
    /// archived registration is left out: nothing is expected to reach it, so its broken path is not news.
    /// </summary>
    public IReadOnlyList<RepositoryUnavailability> Unavailable
    {
        get
        {
            var archived = entries.Where(entry => entry.Archived)
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return quarantine.Values
                .Where(entry => !archived.Contains(entry.Id))
                .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Every persisted repository root, servable or not. The Agent Studio import needs this rather than
    /// <see cref="List"/>: a quarantined registration still occupies its path and its id, so importing
    /// it a second time would only collide.
    /// </summary>
    public IReadOnlySet<string> RegisteredRootPaths => entries
        .Select(entry => entry.RootPath)
        .ToHashSet(PathComparer);

    public RepositoryRegistration Get(string? id, bool includeArchived = false)
    {
        var resolvedId = string.IsNullOrWhiteSpace(id) ? DefaultRepositoryId : id;
        var entry = Find(resolvedId, includeArchived)
                    ?? throw new KeyNotFoundException($"Repository '{resolvedId}' was not found.");
        if (quarantine.TryGetValue(entry.Id, out var unavailable))
            throw new DirectoryNotFoundException(
                $"Repository '{entry.Id}' is {unavailable.Status}: {unavailable.Reason}");
        return entry;
    }

    /// <summary>
    /// Lookup that still sees quarantined registrations, so an operator can repair one by PUT or
    /// archive it. Never use it to serve repository content.
    /// </summary>
    private RepositoryRegistration? Find(string id, bool includeArchived) => entries.FirstOrDefault(entry =>
        string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase) &&
        (includeArchived || !entry.Archived));

    /// <summary>
    /// The servable, non-archived registration that carries the external reference, or null. The
    /// system compares case-insensitively; the id compares case-insensitively too, because Agent
    /// Studio project ids are not case-significant.
    /// </summary>
    public RepositoryRegistration? FindByExternalRef(string system, string id)
    {
        var unavailable = quarantine;
        return entries.FirstOrDefault(entry => !entry.Archived && !unavailable.ContainsKey(entry.Id) &&
            (entry.ExternalRefs ?? []).Any(reference => Matches(reference, system, id)));
    }

    private static bool Matches(RepositoryExternalRef reference, string system, string id) =>
        string.Equals(reference.System, system, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(reference.Id, id, StringComparison.OrdinalIgnoreCase);

    private void EnsureExternalRefsUnique(RepositoryRegistration candidate)
    {
        foreach (var reference in candidate.ExternalRefs ?? [])
        {
            var owner = entries.FirstOrDefault(entry =>
                !string.Equals(entry.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) &&
                (entry.ExternalRefs ?? []).Any(existing => Matches(existing, reference.System, reference.Id)));
            if (owner is not null)
                throw new RepositoryRegistryConflictException(
                    $"External reference '{reference.Key}' is already registered to repository '{owner.Id}'.",
                    "External reference already registered");
        }
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

            EnsureExternalRefsUnique(entry);

            entries = [.. entries, entry];
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
            var existing = Find(string.IsNullOrWhiteSpace(id) ? DefaultRepositoryId : id, includeArchived: true)
                           ?? throw new KeyNotFoundException($"Repository '{id}' was not found.");
            if (existing.Archived)
            {
                throw new RepositoryRegistryValidationException("Archived repositories cannot be edited.");
            }

            var updated = Validate(request with
            {
                Id = existing.Id,
                Sensors = request.Sensors ?? existing.Sensors,
                ExternalRefs = request.ExternalRefs ?? existing.ExternalRefs,
            }, existing.Id);
            EnsureExternalRefsUnique(updated);
            entries = Replace(existing, updated);
            // Validate proved the new root exists inside the allowed roots, so a repaired registration
            // leaves quarantine here rather than waiting for the next restart.
            Release(existing.Id);
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
            var existing = Find(string.IsNullOrWhiteSpace(id) ? DefaultRepositoryId : id, includeArchived: true)
                           ?? throw new KeyNotFoundException($"Repository '{id}' was not found.");
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
            entries = Replace(existing, archived);
            Release(existing.Id);
            // Nothing reads an archived repository's sidecars again; release its filesystem watcher.
            metaIndex.Release(existing.RootPath);
            await PersistAsync(cancellationToken);
            logger.LogInformation(new EventId(1402, "RepositoryArchived"), "Archived repository {RepositoryId}", id);
            return archived;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<RepositoryRegistration> LoadOrSeed()
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
                        Sensors = MergeSupportedSensors(entry.Sensors, entry.RootPath),
                    }).ToArray();
                    quarantine = Quarantine(migrated);
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
            Directory.Exists(root) ? ValidateOptionalDirectory(legacyOptions.GlobalInputsDirectory, root) : null,
            legacyOptions.InputBudgetCharacters,
            SupportedKinds,
            DefaultSensors(root),
            DefaultReviewTokenCap: legacyOptions.DefaultReviewTokenCap);
        RepositoryRegistration[] result = [seeded];
        entries = result;
        quarantine = Quarantine(result);
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(registryPath, JsonSerializer.Serialize(result, JsonOptions()));
        logger.LogInformation(new EventId(1403, "RepositoryRegistrySeeded"),
            "Seeded repository registry {RegistryPath} from legacy root {RepositoryRoot}", registryPath, root);
        return result;
    }

    /// <summary>
    /// Sorts persisted registrations into servable and not. A registration whose directory disappeared
    /// or that points outside the allowed roots must not take the whole host down with it: it is loaded,
    /// reported through <see cref="Unavailable"/> and skipped, while every other registration works.
    /// </summary>
    private IReadOnlyDictionary<string, RepositoryUnavailability> Quarantine(
        IReadOnlyList<RepositoryRegistration> loaded)
    {
        var quarantined = new Dictionary<string, RepositoryUnavailability>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in loaded)
        {
            var unavailable = Inspect(entry);
            if (unavailable is null) continue;
            quarantined[entry.Id] = unavailable;
            logger.LogError(new EventId(1405, "RepositoryQuarantined"),
                "Repository {RepositoryId} at {RepositoryRoot} is {RepositoryStatus}: {RepositoryReason}",
                entry.Id, entry.RootPath, unavailable.Status, unavailable.Reason);
        }
        return quarantined;
    }

    private RepositoryUnavailability? Inspect(RepositoryRegistration entry)
    {
        if (!Directory.Exists(entry.RootPath))
            return new RepositoryUnavailability(entry.Id, entry.DisplayName, entry.RootPath, "unavailable",
                "The repository directory does not exist.");
        try
        {
            EnsureAllowedDirectory(entry.RootPath, "Repository path is outside the configured allowed roots.");
        }
        catch (RepositoryRegistryValidationException exception)
        {
            return new RepositoryUnavailability(entry.Id, entry.DisplayName, entry.RootPath, "quarantined",
                exception.PublicTitle);
        }

        if (entry.GlobalInputsDirectory is null) return null;
        if (!Directory.Exists(entry.GlobalInputsDirectory))
            return new RepositoryUnavailability(entry.Id, entry.DisplayName, entry.GlobalInputsDirectory,
                "unavailable", "The configured global inputs directory does not exist.");
        try
        {
            EnsureAllowedDirectory(entry.GlobalInputsDirectory,
                "Global inputs directory is outside the configured allowed roots.");
        }
        catch (RepositoryRegistryValidationException exception)
        {
            return new RepositoryUnavailability(entry.Id, entry.DisplayName, entry.GlobalInputsDirectory,
                "quarantined", exception.PublicTitle);
        }

        return null;
    }

    private IReadOnlyList<RepositoryRegistration> Replace(
        RepositoryRegistration existing,
        RepositoryRegistration replacement) => entries
        .Select(entry => ReferenceEquals(entry, existing) ? replacement : entry)
        .ToArray();

    private void Release(string id)
    {
        if (!quarantine.ContainsKey(id)) return;
        quarantine = quarantine
            .Where(pair => !string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        logger.LogInformation(new EventId(1406, "RepositoryReleased"),
            "Repository {RepositoryId} left quarantine", id);
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
                "Repository path does not exist", reason: RepositoryRegistryValidationException.RootMissing);
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

        var requestedSensors = request.Sensors ?? DefaultSensors(root);
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

        foreach (var sensor in sensors) ValidateSensorConfiguration(sensor);

        if (request.DefaultReviewTokenCap.HasValue && request.DefaultReviewCostCap.HasValue)
            throw new RepositoryRegistryValidationException("Choose either a default token cap or a default cost cap, not both.");
        if (request.DefaultReviewTokenCap is <= 0 or > 1_000_000_000)
            throw new RepositoryRegistryValidationException("Default review token cap must be between 1 and 1,000,000,000 tokens.");
        if (request.DefaultReviewCostCap is <= 0 or > 1_000_000)
            throw new RepositoryRegistryValidationException("Default review cost cap must be between 0 and 1,000,000.");

        return new RepositoryRegistration(id, request.DisplayName.Trim(), root,
            ValidateOptionalDirectory(request.GlobalInputsDirectory, root), budget, kinds, sensors,
            DefaultReviewTokenCap: request.DefaultReviewTokenCap,
            DefaultReviewCostCap: request.DefaultReviewCostCap,
            ExternalRefs: ValidateExternalRefs(request.ExternalRefs));
    }

    private static IReadOnlyList<RepositoryExternalRef>? ValidateExternalRefs(
        IReadOnlyList<RepositoryExternalRef>? requested)
    {
        if (requested is null || requested.Count == 0) return null;
        if (requested.Count > 16)
            throw new RepositoryRegistryValidationException("A repository carries at most 16 external references.");
        var normalized = new List<RepositoryExternalRef>();
        foreach (var reference in requested)
        {
            var system = reference?.System?.Trim().ToLowerInvariant() ?? "";
            var id = reference?.Id?.Trim() ?? "";
            if (system.Length is < 1 or > 64 || system.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
                throw new RepositoryRegistryValidationException(
                    "External reference system must contain only lowercase letters, numbers, dots, and hyphens.");
            if (id.Length is < 1 or > 128 || id.Any(char.IsControl) || id.Any(char.IsWhiteSpace))
                throw new RepositoryRegistryValidationException(
                    "External reference id is required and may not contain whitespace.");
            var url = string.IsNullOrWhiteSpace(reference!.Url) ? null : reference.Url.Trim();
            if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                                    parsed.Scheme is not ("http" or "https")))
                throw new RepositoryRegistryValidationException("External reference url must be an absolute http(s) URL.");
            if (normalized.Any(existing => Matches(existing, system, id)))
                throw new RepositoryRegistryValidationException($"External reference '{system}:{id}' is listed twice.");
            normalized.Add(new RepositoryExternalRef(system, id, url));
        }

        return normalized;
    }

    private async Task PersistAsync(CancellationToken cancellationToken) =>
        await AtomicFile.WriteAllTextAsync(
            registryPath, JsonSerializer.Serialize(entries, JsonOptions()), cancellationToken);

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

    private IReadOnlyList<RepositorySensorConfiguration> DefaultSensors(string root) =>
        supportedSensors.Select(id => DefaultSensor(id, root)).ToArray();

    private IReadOnlyList<RepositorySensorConfiguration> MergeSupportedSensors(
        IReadOnlyList<RepositorySensorConfiguration>? configured,
        string root)
    {
        var existing = (configured ?? Array.Empty<RepositorySensorConfiguration>())
            .ToDictionary(sensor => sensor.Id, StringComparer.OrdinalIgnoreCase);
        return supportedSensors
            .Select(id => existing.TryGetValue(id, out var sensor)
                ? DropPersistedCommand(MergeDefaultConfiguration(sensor, DefaultSensor(id, root)),
                    DefaultSensor(id, root))
                : DefaultSensor(id, root))
            .ToArray();
    }

    /// <summary>
    /// A registry written before analyzer profiles existed carries the command the sensor used to run.
    /// It is dropped on load and replaced by the host profile for that sensor, so an inherited command
    /// never survives an upgrade of the host that no longer allows one.
    /// </summary>
    private RepositorySensorConfiguration DropPersistedCommand(
        RepositorySensorConfiguration sensor,
        RepositorySensorConfiguration fallback)
    {
        if (profiles.AllowInlineCommands || sensor.Configuration is null ||
            !sensor.Configuration.ContainsKey(AnalyzerSensorConfiguration.CommandKey))
            return sensor;
        var sanitized = sensor.Configuration
            .Where(pair => !string.Equals(pair.Key, AnalyzerSensorConfiguration.CommandKey,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!sanitized.ContainsKey(AnalyzerSensorConfiguration.ProfileKey) &&
            fallback.Configuration?.TryGetValue(AnalyzerSensorConfiguration.ProfileKey, out var profile) == true)
            sanitized[AnalyzerSensorConfiguration.ProfileKey] = profile;
        logger.LogWarning(new EventId(1404, "AnalyzerCommandDropped"),
            "Dropped the persisted analyzer command of sensor {SensorId}; analyzer commands are host-owned",
            sensor.Id);
        return sensor with { Configuration = sanitized };
    }

    /// <summary>
    /// Refuses sensor configuration a client must not be able to write: an executable command, a key
    /// the sensor does not understand, or a profile this host does not offer.
    /// </summary>
    private void ValidateSensorConfiguration(RepositorySensorConfiguration sensor)
    {
        if (sensor.Configuration is null) return;
        var allowed = AnalyzerSensorConfiguration.AllowedKeys(sensor.Id);
        foreach (var key in sensor.Configuration.Keys)
        {
            if (string.Equals(key, AnalyzerSensorConfiguration.CommandKey, StringComparison.OrdinalIgnoreCase))
            {
                if (profiles.AllowInlineCommands) continue;
                throw new RepositoryRegistryValidationException(
                    $"Sensor '{sensor.Id}' may not carry an executable command.",
                    "Analyzer commands are host-owned");
            }

            if (allowed is not null && !allowed.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new RepositoryRegistryValidationException(
                    $"Sensor '{sensor.Id}' does not accept configuration key '{key}'.",
                    "Unsupported sensor configuration key");
        }

        if (sensor.Configuration.TryGetValue(AnalyzerSensorConfiguration.ProfileKey, out var profileId) &&
            !string.IsNullOrWhiteSpace(profileId) && !profiles.TryResolve(sensor.Id, profileId, out _))
            throw new RepositoryRegistryValidationException(
                $"Analyzer profile '{profileId}' is not configured for sensor '{sensor.Id}'.",
                "Unknown analyzer profile");
    }

    private static RepositorySensorConfiguration MergeDefaultConfiguration(
        RepositorySensorConfiguration configured,
        RepositorySensorConfiguration fallback)
    {
        if (configured.Configuration is not null ||
            configured.Id is not ("eslint" or "roslyn" or "sarif" or "tsc"))
            return configured;
        return fallback with { Enabled = configured.Enabled && fallback.Enabled };
    }

    private static RepositorySensorConfiguration DefaultSensor(string id, string root) => id switch
    {
        "architecture" => new RepositorySensorConfiguration(id, ArchitectureSensor.HasTarget(root)),
        "dotnet-build" => new RepositorySensorConfiguration(id, DotNetBuildSensor.HasTarget(root)),
        "angular-compiler" => new RepositorySensorConfiguration(id, AngularCompilerSensor.HasTarget(root)),
        "eslint" => EslintDefault(id, root),
        "roslyn" or "sarif" or "tsc" => new RepositorySensorConfiguration(id, Enabled: false),
        _ => new RepositorySensorConfiguration(id),
    };

    private static RepositorySensorConfiguration EslintDefault(string id, string root)
    {
        var frontend = Path.Combine(root, "frontend");
        var configuration = Path.Combine(frontend, "eslint.config.mjs");
        var manifest = Path.Combine(frontend, "package.json");
        if (!File.Exists(configuration) || !File.Exists(manifest))
            return new RepositorySensorConfiguration(id, Enabled: false);
        return new RepositorySensorConfiguration(
            id,
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AnalyzerSensorConfiguration.ProfileKey] = "eslint-frontend-sarif",
            });
    }
}

public sealed class RepositoryRegistryValidationException : Exception
{
    /// <summary>The repository root does not exist yet; the caller may retry once it does.</summary>
    public const string RootMissing = "root-missing";

    public RepositoryRegistryValidationException(string message, string publicTitle = "Invalid repository configuration",
        Exception? innerException = null, string? reason = null) : base(message, innerException)
    {
        PublicTitle = publicTitle;
        Reason = reason;
    }

    public string PublicTitle { get; }

    /// <summary>A stable, machine-readable reason for the refusal, when the caller can act on it.</summary>
    public string? Reason { get; }
}

/// <summary>A registration collides with another one on a value that must be unique.</summary>
public sealed class RepositoryRegistryConflictException(string message, string publicTitle) : Exception(message)
{
    public string PublicTitle { get; } = publicTitle;
}
