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
    bool Archived = false);

public sealed record RepositoryRegistrationRequest(
    string? Id,
    string DisplayName,
    string RootPath,
    string? GlobalInputsDirectory,
    int? InputBudgetCharacters,
    IReadOnlyList<string>? EnabledReviewKinds);

public sealed class RepositoryRegistry
{
    public const string DefaultRepositoryId = "default";
    public const string RelativeRegistryPath = ".quality-studio/repositories.json";
    private static readonly string[] SupportedKinds = ["code", "security", "performance"];
    private readonly string registryPath;
    private readonly string contentRoot;
    private readonly RepositoryOptions legacyOptions;
    private readonly ILogger<RepositoryRegistry> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<RepositoryRegistration> entries;

    public RepositoryRegistry(IHostEnvironment environment, IOptions<RepositoryOptions> options, ILogger<RepositoryRegistry> logger)
    {
        contentRoot = environment.ContentRootPath;
        legacyOptions = options.Value;
        this.logger = logger;
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

    public RepositoryAccess Access(string? id) => new(Get(id).RootPath);

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

            var updated = Validate(request with { Id = existing.Id }, existing.Id);
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
                    return loaded;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                throw new InvalidOperationException($"Repository registry could not be read: {registryPath}", exception);
            }
        }

        var root = ResolvePath(legacyOptions.RepositoryRoot, contentRoot);
        var displayName = new DirectoryInfo(root).Name;
        var seeded = new RepositoryRegistration(
            DefaultRepositoryId,
            string.IsNullOrWhiteSpace(displayName) ? "Default repository" : displayName,
            root,
            NormalizeOptionalPath(legacyOptions.GlobalInputsDirectory, root),
            legacyOptions.InputBudgetCharacters,
            SupportedKinds);
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
            throw new RepositoryRegistryValidationException($"Repository path does not exist or is not a directory: {root}");
        }

        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            throw new RepositoryRegistryValidationException($"Path is not a Git repository: {root}");
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

        return new RepositoryRegistration(id, request.DisplayName.Trim(), root,
            NormalizeOptionalPath(request.GlobalInputsDirectory, root), budget, kinds);
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

    private static string? NormalizeOptionalPath(string? path, string relativeTo) =>
        string.IsNullOrWhiteSpace(path) ? null : ResolvePath(path, relativeTo);

    private static string Slugify(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed class RepositoryRegistryValidationException(string message) : Exception(message);
