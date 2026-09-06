namespace QualityStudio.Api;

public sealed class RepositoryOptions
{
    public const string SectionName = "QualityStudio";

    public string RepositoryRoot { get; set; } = ".";

    public string[] AllowedOrigins { get; set; } = ["http://localhost:4200"];

    public string[] AllowedRoots { get; set; } = [];

    public string? GlobalInputsDirectory { get; set; }

    public int InputBudgetCharacters { get; set; } = AgentOrchestrator.CodeQuality.InputResolver.DefaultBudgetCharacters;

    public long? DefaultReviewTokenCap { get; set; } = 100_000;

    public ApiSecurityOptions Security { get; set; } = new();
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
