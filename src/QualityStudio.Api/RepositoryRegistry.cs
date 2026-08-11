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
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<RepositoryRegistration> entries;

    public RepositoryRegistry(IHostEnvironment environment, IOptions<RepositoryOptions> options,
        SensorRegistry sensors, ILogger<RepositoryRegistry> logger, ReviewMetaIndex metaIndex)
    {
        contentRoot = environment.ContentRootPath;
        legacyOptions = options.Value;
        supportedSensors = sensors.List().Select(sensor => sensor.Id).ToArray();
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
                Sensors = request.Sensors ?? existing.Sensors,
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
                        Sensors = MergeSupportedSensors(entry.Sensors, entry.RootPath),
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
            DefaultSensors(root),
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

        var requestedSensors = request.Sensors ?? DefaultSensors(root);
        if (requestedSensors.Any(sensor => string.IsNullOrWhiteSpace(sensor.Id)))
        {
            throw new RepositoryRegistryValidationException("Every sensor configuration requires an id.");
        }

        var sensors = requestedSensors
            .Select(sensor => ApplyDefaultConfiguration(sensor with
            {
                Id = sensor.Id.Trim().ToLowerInvariant(),
                Configuration = sensor.Configuration is null
                    ? null
                    : new Dictionary<string, string>(sensor.Configuration, StringComparer.Ordinal),
            }, root))
            .ToArray();
        if (sensors.Length == 0 ||
            sensors.Any(sensor => !supportedSensors.Contains(sensor.Id, StringComparer.Ordinal)) ||
            sensors.Select(sensor => sensor.Id).Distinct(StringComparer.Ordinal).Count() != sensors.Length)
        {
            throw new RepositoryRegistryValidationException(
                $"Sensors must be a unique selection of: {string.Join(", ", supportedSensors)}.");
        }

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

    private IReadOnlyList<RepositorySensorConfiguration> DefaultSensors(string repositoryRoot) =>
        supportedSensors.Select(id => DefaultSensor(id, repositoryRoot)).ToArray();

    private IReadOnlyList<RepositorySensorConfiguration> MergeSupportedSensors(
        IReadOnlyList<RepositorySensorConfiguration>? configured,
        string repositoryRoot)
    {
        var existing = (configured ?? Array.Empty<RepositorySensorConfiguration>())
            .ToDictionary(sensor => sensor.Id, StringComparer.OrdinalIgnoreCase);
        return supportedSensors
            .Select(id => existing.TryGetValue(id, out var sensor)
                ? ApplyDefaultConfiguration(sensor, repositoryRoot)
                : DefaultSensor(id, repositoryRoot))
            .ToArray();
    }

    private static RepositorySensorConfiguration ApplyDefaultConfiguration(
        RepositorySensorConfiguration sensor,
        string repositoryRoot)
    {
        if (sensor.Configuration is not null) return sensor;
        var defaults = DefaultSensor(sensor.Id, repositoryRoot);
        return defaults.Configuration is null
            ? sensor
            : sensor with { Configuration = defaults.Configuration };
    }

    private static RepositorySensorConfiguration DefaultSensor(string id, string repositoryRoot) => id switch
    {
        "roslyn" => new RepositorySensorConfiguration(
            id,
            Enabled: HasRootDotNetTarget(repositoryRoot),
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["format"] = "dotnet-build",
                ["command"] = "dotnet build {repositoryRoot} --configuration Release --nologo -p:GenerateFullPaths=true",
                ["reportPath"] = ".quality/analyzers/dotnet-build.txt",
            }),
        "tsc" => new RepositorySensorConfiguration(
            id,
            Enabled: HasFrontendTypeScriptTarget(repositoryRoot),
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "node node_modules/typescript/bin/tsc -p tsconfig.app.json --noEmit --pretty false",
                ["reportPath"] = ".quality/analyzers/tsc.txt",
                ["workingDirectory"] = "frontend",
            }),
        "sarif" or "eslint" => new RepositorySensorConfiguration(id, Enabled: false),
        _ => new RepositorySensorConfiguration(id),
    };

    private static bool HasRootDotNetTarget(string repositoryRoot) =>
        Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
        Directory.EnumerateFiles(repositoryRoot, "*.slnx", SearchOption.TopDirectoryOnly).Any() ||
        Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.TopDirectoryOnly).Any();

    private static bool HasFrontendTypeScriptTarget(string repositoryRoot) =>
        File.Exists(Path.Combine(repositoryRoot, "frontend", "tsconfig.app.json")) &&
        File.Exists(Path.Combine(repositoryRoot, "frontend", "package-lock.json"));
}

public sealed class RepositoryRegistryValidationException : Exception
{
    public RepositoryRegistryValidationException(string message, string publicTitle = "Invalid repository configuration",
        Exception? innerException = null) : base(message, innerException) => PublicTitle = publicTitle;

    public string PublicTitle { get; }
}
