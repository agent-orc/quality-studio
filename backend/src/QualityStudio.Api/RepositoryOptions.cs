using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed class RepositoryOptions
{
    public const string SectionName = "QualityStudio";

    public string RepositoryRoot { get; set; } = ".";

    /// <summary>
    /// Where this host keeps everything its runs generate, one folder per analysed project. A
    /// relative path resolves against the content root. Unset means the per-user default that
    /// <see cref="QualityDataRoot"/> documents, which is what a local install wants; a container
    /// points it at a mounted volume so the data outlives the container.
    /// </summary>
    public string? DataRoot { get; set; }

    /// <summary>
    /// The configured data root as an absolute path, or null for the default. Read straight from
    /// configuration rather than from the bound options: the stores resolve paths while the host is
    /// still being built, before <c>IOptions</c> can be asked.
    /// </summary>
    public static string? ResolveDataRoot(string? configured, string contentRootPath) =>
        string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.GetFullPath(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRootPath, configured));

    public string[] AllowedOrigins { get; set; } = ["http://localhost:4200"];

    public string[] AllowedRoots { get; set; } = [];

    public string? GlobalInputsDirectory { get; set; }

    public int InputBudgetCharacters { get; set; } = AgentOrchestrator.CodeQuality.InputResolver.DefaultBudgetCharacters;

    public long? DefaultReviewTokenCap { get; set; } = 100_000;

    public ApiSecurityOptions Security { get; set; } = new();

    public AnalyzerProfileOptions AnalyzerProfiles { get; set; } = new();

    public LimitOptions Limits { get; set; } = new();
}

/// <summary>Where the host-owned analyzer profiles come from, and whether inline commands are back on.</summary>
public sealed class AnalyzerProfileOptions
{
    /// <summary>
    /// Path to a host-owned analyzer-profiles.json; relative paths resolve against the content root.
    /// When unset, the API looks for <c>analyzer-profiles.json</c> next to the content root and falls
    /// back to the embedded defaults.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Re-enables free-form <c>command</c> entries in repository sensor configuration. Off by default:
    /// a client that can write sensor configuration would otherwise choose what the host executes.
    /// </summary>
    public bool AllowInlineCommands { get; set; }

    public static AnalyzerProfileCatalog CreateCatalog(AnalyzerProfileOptions options, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Path;
        var path = string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(contentRootPath, AnalyzerProfileCatalog.DefaultFileName)
            : System.IO.Path.IsPathRooted(configured)
                ? configured
                : System.IO.Path.Combine(contentRootPath, configured);
        if (!string.IsNullOrWhiteSpace(configured) && !File.Exists(path))
            throw new InvalidOperationException($"Configured analyzer profile file does not exist: {path}");
        return AnalyzerProfileCatalog.Load(path, options.AllowInlineCommands);
    }
}

/// <summary>The operating lids: how much one request may return and how long probes stay cached.</summary>
public sealed class LimitOptions
{
    /// <summary>Largest file body <c>GET /api/file</c> returns in full. Above it the response is a preview.</summary>
    public long MaxFileBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>How much of an oversized file the preview carries.</summary>
    public long LargeFilePreviewBytes { get; set; } = 64 * 1024;

    /// <summary>How long a sensor availability probe is reused before the tool is asked again.</summary>
    public int SensorAvailabilityCacheSeconds { get; set; } = 300;

    /// <summary>How many project dashboards stay cached before the least recently used one is dropped.</summary>
    public int ProjectDashboardCacheEntries { get; set; } = 32;

    public void Validate()
    {
        if (MaxFileBytes is < 4 * 1024 or > 256L * 1024 * 1024)
            throw new InvalidOperationException("QualityStudio:Limits:MaxFileBytes must be between 4 KiB and 256 MiB.");
        if (LargeFilePreviewBytes < 1024 || LargeFilePreviewBytes > MaxFileBytes)
            throw new InvalidOperationException(
                "QualityStudio:Limits:LargeFilePreviewBytes must be between 1 KiB and MaxFileBytes.");
        if (SensorAvailabilityCacheSeconds is < 0 or > 86_400)
            throw new InvalidOperationException(
                "QualityStudio:Limits:SensorAvailabilityCacheSeconds must be between 0 and 86,400.");
        if (ProjectDashboardCacheEntries is < 1 or > 1024)
            throw new InvalidOperationException(
                "QualityStudio:Limits:ProjectDashboardCacheEntries must be between 1 and 1,024.");
    }
}

public sealed class ApiSecurityOptions
{
    public const string LocalMode = "Local";
    public const string HostedMode = "Hosted";

    public string Mode { get; set; } = LocalMode;

    /// <summary>
    /// Deliberate override for a Local-mode host that binds beyond loopback. Local mode authenticates
    /// nobody, so this publishes an unauthenticated registrar API; see docs/deployment.md.
    /// </summary>
    public bool AllowNonLoopbackLocalMode { get; set; }

    public bool RequireHttps { get; set; } = true;
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;
    public int MaxConcurrentRequests { get; set; } = 32;
    public int SpendRequestsPerMinute { get; set; } = 5;
    public List<ApiClientOptions> Clients { get; set; } = [];
}

public sealed class ApiClientOptions
{
    public string Id { get; set; } = string.Empty;
    public string CredentialSha256 { get; set; } = string.Empty;
    public string[] Repositories { get; set; } = [];
    public bool CanRegisterRepositories { get; set; }
}
