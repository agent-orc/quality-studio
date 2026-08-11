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

    public AgentOrchestrator.CodeQuality.ReviewContentLimits ContentLimits { get; set; } = new();

    public List<AnalyzerProfileOptions> AnalyzerProfiles { get; set; } = [];
}

public sealed class ApiSecurityOptions
{
    public const string LocalMode = "Local";
    public const string HostedMode = "Hosted";

    public string Mode { get; set; } = LocalMode;
    public bool RequireHttps { get; set; } = true;
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;
    public int MaxConcurrentRequests { get; set; } = 32;
    public int SpendRequestsPerMinute { get; set; } = 5;
    public bool CommandBackedAnalyzersEnabled { get; set; }
    public string Audience { get; set; } = "quality-studio-api";
    public string? RevocationFile { get; set; }
    public List<ApiClientOptions> Clients { get; set; } = [];
}

public sealed class AnalyzerProfileOptions
{
    public string Id { get; set; } = string.Empty;
    public string SensorId { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public string[] Arguments { get; set; } = [];
    public string ReportPath { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public string? ProducerVersion { get; set; }
}

public sealed class ApiClientOptions
{
    public string Id { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string CredentialSha256 { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public string[] Repositories { get; set; } = [];
    public string[] Roles { get; set; } = [];
}
