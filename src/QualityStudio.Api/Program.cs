using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using QualityStudio.Api;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.Configure<RepositoryOptions>(builder.Configuration.GetSection(RepositoryOptions.SectionName));
builder.Services.AddSingleton<ApiSecurity>();
builder.Services.AddSingleton<ReviewMetaIndex>();
builder.Services.AddSingleton<RepositoryRegistry>();
builder.Services.AddSingleton<RepositoryHierarchyCache>();
builder.Services.AddSingleton<ProjectDashboardService>();
builder.Services.AddSingleton<RepositorySnapshotPrewarmer>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RepositorySnapshotPrewarmer>());
builder.Services.AddSingleton<StalenessEvaluator>();
builder.Services.AddSingleton<ReviewModelCatalog>();
builder.Services.AddSingleton<QualityReportBuilder>();
builder.Services.AddSingleton<InputResolver>();
builder.Services.AddSingleton<GuidelineStore>();
builder.Services.AddTransient<GuidelineImpactAnalyzer>();
builder.Services.AddSingleton<GitleaksBinaryResolver>();
builder.Services.AddSingleton<GitleaksSecurityScanner>();
builder.Services.AddSingleton<DependencyVulnerabilitySensor>();
builder.Services.AddSingleton<BoundaryInventorySensor>();
builder.Services.AddSingleton<AttackCatalogueResolver>();
builder.Services.AddSingleton<AttackCoverageService>();
builder.Services.AddSingleton<CoverageSensor>();
builder.Services.AddSingleton<SarifSensor>();
builder.Services.AddSingleton<RoslynAnalyzerSensor>();
builder.Services.AddSingleton<EslintAnalyzerSensor>();
builder.Services.AddSingleton<TypeScriptAnalyzerSensor>();
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<GitleaksSecurityScanner>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<DependencyVulnerabilitySensor>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<BoundaryInventorySensor>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<CoverageSensor>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<SarifSensor>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<RoslynAnalyzerSensor>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<EslintAnalyzerSensor>());
builder.Services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<TypeScriptAnalyzerSensor>());
builder.Services.AddSingleton<SensorRegistry>();
builder.Services.Configure<AgentStudioTaskOptions>(
    builder.Configuration.GetSection(AgentStudioTaskOptions.SectionName));
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentStudioTaskOptions>>().Value);
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<AgentStudioTaskClient>();
builder.Services.Configure<ReviewJobsOptions>(builder.Configuration.GetSection(ReviewJobsOptions.SectionName));
builder.Services.AddSingleton<IReviewExecutorFactory, ReviewExecutorFactory>();
builder.Services.AddSingleton<ReviewJobService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ReviewJobService>());
builder.Services.AddSingleton(_ => new QuotaService(
    probes: [new ClaudeOAuthUsageProbe(), new CodexSessionLogProbe()],
    store: FileQuotaCacheStore.Global()));
var corsOptions = builder.Configuration.GetSection(RepositoryOptions.SectionName).Get<RepositoryOptions>()
    ?? new RepositoryOptions();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = corsOptions.Security.MaxRequestBodyBytes;
    options.Limits.MaxConcurrentConnections = corsOptions.Security.MaxConcurrentRequests;
});
builder.Services.AddCors(options => options.AddPolicy("dev-frontend", policy =>
    policy.WithOrigins(corsOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetConcurrencyLimiter("api-host", _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<ApiSecurity>().MaxConcurrentRequests,
            QueueLimit = 0,
        }));
    options.AddPolicy("spend", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.Headers[ApiSecurity.ClientIdHeader].ToString() is { Length: > 0 } clientId
            ? clientId
            : context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<ApiSecurity>().SpendRequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = exception switch
    {
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid repository path"),
        RepositoryRegistryValidationException validation => (StatusCodes.Status400BadRequest, validation.PublicTitle),
        SensorNotFoundException => (StatusCodes.Status404NotFound, "Sensor not found"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Repository not found"),
        FileNotFoundException => (StatusCodes.Status404NotFound, "File not found"),
        DirectoryNotFoundException => (StatusCodes.Status503ServiceUnavailable, "Repository unavailable"),
        StalenessScanException => (StatusCodes.Status422UnprocessableEntity, "Repository scan failed"),
        QualityReportException => (StatusCodes.Status422UnprocessableEntity, "Quality report failed"),
        InputFormatException => (StatusCodes.Status422UnprocessableEntity, "Review input is invalid"),
        JsonException => (StatusCodes.Status422UnprocessableEntity, "Repository security metadata is invalid"),
        SecurityScannerUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Security scanner unavailable"),
        HttpRequestException => (StatusCodes.Status502BadGateway, "Agent Studio request failed"),
        InvalidOperationException => (StatusCodes.Status503ServiceUnavailable, "Agent Studio target unavailable"),
        FindingStateConflictException => (StatusCodes.Status409Conflict, "Finding state changed"),
        FindingAssessmentConflictException => (StatusCodes.Status409Conflict, "Finding assessment changed"),
        FindingSuppressionConflictException => (StatusCodes.Status409Conflict, "Finding suppressions changed"),
        ReviewHistoryConflictException => (StatusCodes.Status409Conflict, "Committed review history conflict"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected API error"),
    };
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("QualityStudio.Api.Errors");
    logger.LogError(new EventId(1000, "ApiRequestFailed"), exception, "API request failed with status {StatusCode}", status);
    await Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
}));
app.UseStatusCodePages();
app.UseCors("dev-frontend");
app.UseRouting();

var apiSecurity = app.Services.GetRequiredService<ApiSecurity>();
if (apiSecurity.RequireHttps)
{
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    if (apiSecurity.RequireHttps && !context.Request.IsHttps)
    {
        await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "HTTPS is required").ExecuteAsync(context);
        return;
    }

    if (context.Request.ContentLength > apiSecurity.MaxRequestBodyBytes)
    {
        await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body is too large").ExecuteAsync(context);
        return;
    }
    var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (bodySizeFeature is { IsReadOnly: false }) bodySizeFeature.MaxRequestBodySize = apiSecurity.MaxRequestBodyBytes;

    var identity = apiSecurity.Authenticate(context);
    if (identity is null)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required").ExecuteAsync(context);
        return;
    }
    apiSecurity.SetIdentity(context, identity);

    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
        HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
    {
        if (!apiSecurity.IsMutationClientHeaderValid(context, identity))
        {
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: "A matching X-Client-Id is required for mutations").ExecuteAsync(context);
            return;
        }
    }

    var path = context.Request.Path.Value ?? string.Empty;
    var repositoryId = RouteRepositoryId(context);
    var isRepositoryCollection = string.Equals(path, "/api/repos", StringComparison.OrdinalIgnoreCase);
    var isReportCollection = string.Equals(path, "/api/report", StringComparison.OrdinalIgnoreCase);
    var isImport = string.Equals(path, "/api/repos/import-from-agent-studio", StringComparison.OrdinalIgnoreCase);
    if ((HttpMethods.IsPost(context.Request.Method) && isRepositoryCollection) || isImport)
    {
        if (!identity.CanRegisterRepositories)
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Repository registration is not permitted")
                .ExecuteAsync(context);
            return;
        }
    }
    else if (repositoryId is not null && !identity.CanAccess(repositoryId))
    {
        await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Repository not found").ExecuteAsync(context);
        return;
    }
    else if (repositoryId is null && !isRepositoryCollection && !isReportCollection &&
             !string.Equals(path, "/api/quotas", StringComparison.OrdinalIgnoreCase) &&
             !identity.CanAccess(RepositoryRegistry.DefaultRepositoryId))
    {
        await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Repository not found").ExecuteAsync(context);
        return;
    }

    await next();
});
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "QualityStudio.Api" }));

app.MapGet("/api/repos", (HttpContext context, bool? includeArchived, RepositoryRegistry registry,
    RepositorySnapshotPrewarmer prewarmer, ApiSecurity security) =>
{
    var repositories = registry.List(includeArchived == true)
        .Where(repository => security.Identity(context).CanAccess(repository.Id))
        .ToArray();
    prewarmer.QueueAll(repositories);
    return Results.Ok(new
    {
        repositories,
        defaultRepositoryId = security.Identity(context).CanAccess(RepositoryRegistry.DefaultRepositoryId)
            ? RepositoryRegistry.DefaultRepositoryId
            : null,
    });
});

app.MapPost("/api/repos", async (RepositoryRegistrationRequest request, RepositoryRegistry registry,
    RepositorySnapshotPrewarmer prewarmer, CancellationToken cancellationToken) =>
{
    var created = await registry.CreateAsync(request, cancellationToken);
    prewarmer.Queue(created);
    return Results.Created($"/api/repos/{created.Id}", created);
});

app.MapPost("/api/repos/import-from-agent-studio", ImportFromAgentStudio);

app.MapPut("/api/repos/{repoId}", async (string repoId, RepositoryRegistrationRequest request,
    RepositoryRegistry registry, RepositorySnapshotPrewarmer prewarmer, CancellationToken cancellationToken) =>
{
    var updated = await registry.UpdateAsync(repoId, request, cancellationToken);
    prewarmer.Queue(updated);
    return Results.Ok(updated);
});

app.MapDelete("/api/repos/{repoId}", async (string repoId, RepositoryRegistry registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.ArchiveAsync(repoId, cancellationToken)));

app.MapGet("/api/tree", Tree);
app.MapGet("/api/repos/{repoId}/tree", Tree);
app.MapGet("/api/risk", Risk);
app.MapGet("/api/repos/{repoId}/risk", Risk);
app.MapGet("/api/project", ProjectDashboard);
app.MapGet("/api/repos/{repoId}/project", ProjectDashboard);
app.MapGet("/api/file", FileContent);
app.MapGet("/api/repos/{repoId}/file", FileContent);
app.MapGet("/api/inputs", Inputs);
app.MapGet("/api/repos/{repoId}/inputs", Inputs);
app.MapGet("/api/guidelines", Guidelines);
app.MapGet("/api/repos/{repoId}/guidelines", Guidelines);
app.MapPost("/api/guidelines", CreateGuideline);
app.MapPost("/api/repos/{repoId}/guidelines", CreateGuideline);
app.MapPut("/api/guidelines/{guidelineId}", UpdateGuideline);
app.MapPut("/api/repos/{repoId}/guidelines/{guidelineId}", UpdateGuideline);
app.MapDelete("/api/guidelines/{guidelineId}", DeleteGuideline);
app.MapDelete("/api/repos/{repoId}/guidelines/{guidelineId}", DeleteGuideline);
app.MapPost("/api/guidelines/catalog/{catalogueId}/install", InstallGuideline);
app.MapPost("/api/repos/{repoId}/guidelines/catalog/{catalogueId}/install", InstallGuideline);
app.MapPost("/api/guidelines/impact", GuidelineImpact);
app.MapPost("/api/repos/{repoId}/guidelines/impact", GuidelineImpact);
app.MapGet("/api/scan", Scan);
app.MapGet("/api/repos/{repoId}/scan", Scan);
app.MapGet("/api/security/scan", SecurityScan);
app.MapGet("/api/repos/{repoId}/security/scan", SecurityScan);
app.MapGet("/api/security/attack-coverage", AttackCoverage);
app.MapGet("/api/repos/{repoId}/security/attack-coverage", AttackCoverage);
app.MapPost("/api/security/attack-coverage/judgements", RecordAttackJudgement).RequireRateLimiting("spend");
app.MapPost("/api/repos/{repoId}/security/attack-coverage/judgements", RecordAttackJudgement).RequireRateLimiting("spend");
app.MapGet("/api/sensors", Sensors);
app.MapGet("/api/repos/{repoId}/sensors", Sensors);
app.MapPost("/api/sensors/{id}/scan", SensorScan);
app.MapPost("/api/repos/{repoId}/sensors/{id}/scan", SensorScan);
app.MapGet("/api/usage", Usage);
app.MapGet("/api/repos/{repoId}/usage", Usage);
app.MapGet("/api/report", Report);
app.MapGet("/api/repos/{repoId}/report", Report);
app.MapGet("/api/quotas", Quotas);
app.MapGet("/api/models", (ReviewModelCatalog catalog) => Results.Ok(catalog.Snapshot));

app.MapPost("/api/review", StartReview).RequireRateLimiting("spend");
app.MapPost("/api/repos/{repoId}/review", StartReview).RequireRateLimiting("spend");
app.MapPost("/api/review/estimate", EstimateReview);
app.MapPost("/api/repos/{repoId}/review/estimate", EstimateReview);
app.MapGet("/api/review/runs", ReviewRuns);
app.MapGet("/api/repos/{repoId}/review/runs", ReviewRuns);
app.MapGet("/api/review/history", ReviewHistory);
app.MapGet("/api/repos/{repoId}/review/history", ReviewHistory);
app.MapGet("/api/review/history/{id}", ReviewHistoryRun);
app.MapGet("/api/repos/{repoId}/review/history/{id}", ReviewHistoryRun);
app.MapGet("/api/review/runs/{id}", ReviewRun);
app.MapGet("/api/repos/{repoId}/review/runs/{id}", ReviewRun);
app.MapPost("/api/review/runs/{id}/pause", PauseReview);
app.MapPost("/api/repos/{repoId}/review/runs/{id}/pause", PauseReview);
app.MapPost("/api/review/runs/{id}/resume", ResumeReview);
app.MapPost("/api/repos/{repoId}/review/runs/{id}/resume", ResumeReview);
app.MapDelete("/api/review/runs/{id}", CancelReview);
app.MapDelete("/api/repos/{repoId}/review/runs/{id}", CancelReview);

app.MapGet("/api/handover", HandoverConfiguration);
app.MapGet("/api/repos/{repoId}/handover", HandoverConfiguration);
app.MapPost("/api/handover", Handover).RequireRateLimiting("spend");
app.MapPost("/api/repos/{repoId}/handover", Handover).RequireRateLimiting("spend");
app.MapPost("/api/threads", MutateThread);
app.MapPost("/api/repos/{repoId}/threads", MutateThread);
app.MapPost("/api/findings/state", MutateFindingState);
app.MapPost("/api/repos/{repoId}/findings/state", MutateFindingState);
app.MapGet("/api/findings/policy", FindingPolicy);
app.MapGet("/api/repos/{repoId}/findings/policy", FindingPolicy);
app.MapPost("/api/findings/assessment", MutateFindingAssessment);
app.MapPost("/api/repos/{repoId}/findings/assessment", MutateFindingAssessment);
app.MapPost("/api/findings/suppressions/exact", SuppressExactFinding);
app.MapPost("/api/repos/{repoId}/findings/suppressions/exact", SuppressExactFinding);
app.MapPost("/api/findings/suppressions/preview", PreviewFindingSuppression);
app.MapPost("/api/repos/{repoId}/findings/suppressions/preview", PreviewFindingSuppression);
app.MapPut("/api/findings/suppressions/{id}", SaveFindingSuppression);
app.MapPut("/api/repos/{repoId}/findings/suppressions/{id}", SaveFindingSuppression);

app.Run();

static async Task<IResult> Tree(HttpContext context, string? path, RepositoryRegistry registry,
    RepositoryHierarchyCache hierarchyCache, InputResolver inputResolver, ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var requested = repository.NormalizeRelativePath(path);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    var snapshot = hierarchyCache.Get(
        repository.Root, inputResolver, globalDirectory, registration.InputBudgetCharacters);
    var coverage = CoverageSnapshot.Load(repository.Root);
    var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        snapshot.GitState + "\0" + requested + "\0" + coverage?.MeasuredAt)))}\"";
    context.Response.Headers.ETag = etag;
    if (context.Request.Headers.IfNoneMatch.Any(value => value!.Split(',').Select(candidate => candidate.Trim())
            .Any(candidate => candidate == "*" || StringComparer.Ordinal.Equals(candidate, etag))))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }
    var projects = snapshot.Roots;
    var findingStates = await new FindingStateStore(repository.Root).ReadAsync(cancellationToken);
    var currentCommit = CoverageSensor.GitValue(repository.Root, "rev-parse", "--verify", "HEAD");
    IReadOnlyList<HierarchyNode> selected = requested == "."
        ? projects
        : Flatten(projects).Where(node => string.Equals(node.Path, requested, StringComparison.Ordinal)).ToArray();
    if (selected.Count == 0)
    {
        return Results.NotFound(new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Tree path not found",
            Detail = $"No hierarchy node exists at '{requested}'.",
        });
    }

    logger.LogInformation(new EventId(1100, "TreeLoaded"),
        "Loaded {NodeCount} tree roots for repository {RepositoryId} at {RepositoryPath} in {ElapsedMilliseconds} ms",
        selected.Count, registration.Id, requested, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new TreeResponse(requested,
        selected.Select(node => TreeNodeResponse.From(node, findingStates, coverage, currentCommit)).ToArray()));
}

static IResult ProjectDashboard(
    HttpContext context,
    RepositoryRegistry registry,
    RepositoryHierarchyCache hierarchyCache,
    ProjectDashboardService dashboards,
    InputResolver inputResolver,
    ILogger<Program> logger)
{
    var started = Stopwatch.GetTimestamp();
    var (registration, repository) = ResolveRepository(context, registry);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    var hierarchy = hierarchyCache.GetMeasured(
        repository.Root, inputResolver, globalDirectory, registration.InputBudgetCharacters);
    var snapshot = hierarchy.Snapshot;
    var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.GitState + "\0project-dashboard-v1")))}\"";
    context.Response.Headers.ETag = etag;
    if (context.Request.Headers.IfNoneMatch.Any(value => value!.Split(',').Select(candidate => candidate.Trim())
            .Any(candidate => candidate == "*" || StringComparer.Ordinal.Equals(candidate, etag))))
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    var projection = dashboards.GetMeasured(repository.Root, snapshot);
    var dashboard = projection.Dashboard;
    var durationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    context.Response.Headers["Server-Timing"] = string.Join(", ",
        $"git-status;dur={hierarchy.GitStatusMilliseconds:F2}",
        $"cache-wait;dur={hierarchy.CacheWaitMilliseconds:F2}",
        $"scan;dur={hierarchy.ScanMilliseconds:F2}",
        $"review-meta-discovery;dur={hierarchy.ReviewMetaDiscoveryMilliseconds:F2}",
        $"projection;dur={projection.ProjectionMilliseconds:F2}");
    var switchEvent = JsonSerializer.Serialize(new
    {
        @event = "qs.repository.switch.backend",
        repositoryId = registration.Id,
        cache = hierarchy.CacheHit && projection.CacheHit ? "warm" : "cold",
        durationMs = Math.Round(durationMilliseconds, 2),
        phases = new
        {
            gitStatusMs = Math.Round(hierarchy.GitStatusMilliseconds, 2),
            cacheWaitMs = Math.Round(hierarchy.CacheWaitMilliseconds, 2),
            scanMs = Math.Round(hierarchy.ScanMilliseconds, 2),
            reviewMetaDiscoveryMs = Math.Round(hierarchy.ReviewMetaDiscoveryMilliseconds, 2),
            projectionMs = Math.Round(projection.ProjectionMilliseconds, 2),
        },
        fileCount = dashboard.Metrics.FileCount,
    });
    logger.LogInformation(new EventId(1111, "RepositorySwitchPhases"), "{RepositorySwitchEvent}", switchEvent);
    logger.LogInformation(new EventId(1110, "ProjectDashboardLoaded"),
        "Loaded project dashboard for repository {RepositoryId} with {FileCount} files in {ElapsedMilliseconds} ms",
        registration.Id, dashboard.Metrics.FileCount, Math.Round(durationMilliseconds, 2));
    return Results.Ok(dashboard);
}

static async Task<IResult> FileContent(HttpContext context, string? path, RepositoryRegistry registry,
    ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(path);
    var absolute = repository.ResolveFile(relative);
    var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
    var (encoding, content) = DecodeFileContent(bytes);
    var lineEnding = DetectLineEnding(content);
    var findingStates = await new FindingStateStore(repository.Root).ReadAsync(cancellationToken);
    var policy = new FindingPolicySnapshot(
        await new FindingAssessmentStore(repository.Root).ReadAsync(cancellationToken),
        await new FindingSuppressionStore(repository.Root).ReadAsync(cancellationToken),
        DateTimeOffset.UtcNow);
    var coverage = CoverageProjection.ForPath(
        CoverageSnapshot.Load(repository.Root),
        CoverageSensor.GitValue(repository.Root, "rev-parse", "--verify", "HEAD"),
        relative,
        file: true);
    logger.LogInformation(new EventId(1101, "FileLoaded"),
        "Loaded {FilePath} from repository {RepositoryId} ({SizeBytes} bytes, {Encoding}, {LineEnding}) in {ElapsedMilliseconds} ms",
        relative, registration.Id, bytes.LongLength, encoding, lineEnding, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new FileResponse(relative, content, repository.ReadMetaDocuments(relative, findingStates, policy),
        bytes.LongLength, lineEnding, encoding, coverage));
}

static async Task<IResult> Risk(HttpContext context, int? days, RepositoryRegistry registry,
    RepositoryHierarchyCache hierarchyCache, InputResolver inputResolver, CancellationToken cancellationToken)
{
    var window = days ?? 90;
    if (window is < 1 or > 3650) throw new ArgumentException("Risk churn window must be between 1 and 3,650 days.");
    var (registration, repository) = ResolveRepository(context, registry);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    var roots = hierarchyCache.Get(repository.Root, inputResolver, globalDirectory,
        registration.InputBudgetCharacters).Roots;
    var states = await new FindingStateStore(repository.Root).ReadAsync(cancellationToken);
    var snapshot = CoverageSnapshot.Load(repository.Root);
    var currentCommit = CoverageSensor.GitValue(repository.Root, "rev-parse", "--verify", "HEAD");
    var churn = new GitChurnAnalyzer().Analyze(repository.Root, window);
    var files = Flatten(roots).Where(node => node.Level == ReviewLevel.File)
        .DistinctBy(node => node.Path, StringComparer.Ordinal).ToArray();
    var maxChanges = Math.Max(1, files.Select(file => churn.GetValueOrDefault(file.Path)).DefaultIfEmpty().Max());
    var rows = files.Select(file =>
    {
        var kind = KindStateResponse.From(file, file.AggregatedStates[ReviewKind.Code], states);
        var fileCoverage = CoverageProjection.ForPath(snapshot, currentCommit, file.Path, file: true);
        var changes = churn.GetValueOrDefault(file.Path);
        decimal? score = kind.Score.HasValue && fileCoverage.LinePercent.HasValue
            ? Math.Round(
                (100 - kind.Score.Value) * 0.4m +
                (100 - fileCoverage.LinePercent.Value) * 0.4m +
                changes * 20m / maxChanges,
                2,
                MidpointRounding.AwayFromZero)
            : null;
        return new RiskRowResponse(file.Path, file.Name, kind.Score, kind.Band, kind.Overall,
            fileCoverage, changes, score);
    }).OrderByDescending(row => row.RiskScore.HasValue).ThenByDescending(row => row.RiskScore)
        .ThenByDescending(row => row.Changes).ThenBy(row => row.Path, StringComparer.Ordinal).ToArray();
    var matrix = rows.GroupBy(row => new
        {
            Grade = row.GradeScore switch
            {
                null => "unknown",
                < 70 => "weak",
                < 80 => "mediocre",
                _ => "strong",
            },
            Coverage = row.Coverage.LinePercent switch
            {
                null => "unknown",
                < 50 => "low",
                < 80 => "medium",
                _ => "high",
            },
        })
        .Select(group => new RiskMatrixCellResponse(group.Key.Grade, group.Key.Coverage,
            group.Count(), group.Sum(row => row.Changes)))
        .OrderBy(cell => cell.Grade, StringComparer.Ordinal).ThenBy(cell => cell.Coverage, StringComparer.Ordinal)
        .ToArray();
    return Results.Ok(new RiskResponse(window, currentCommit, rows, matrix));
}

static async Task<IResult> MutateFindingState(HttpContext context, FindingStateMutationRequest request,
    RepositoryRegistry registry, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(request.Path);
    var metaPath = repository.FindMetaDocument(relative, request.Kind);
    FindingIdentityRecord identity;
    using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken)))
    {
        var finding = metadata.RootElement.GetProperty("findings").EnumerateArray().FirstOrDefault(candidate =>
            candidate.TryGetProperty("fingerprint", out var value) && value.GetString() == request.Fingerprint);
        if (finding.ValueKind == JsonValueKind.Undefined)
            throw new KeyNotFoundException($"Finding '{request.Fingerprint}' was not found in the selected review.");
        identity = new FindingIdentityRecord(
            request.Fingerprint,
            finding.GetProperty("id").GetString()!,
            finding.GetProperty("locations")[0].GetProperty("path").GetString()!,
            finding.GetProperty("ruleId").GetString()!);
    }

    var state = request.State switch
    {
        "open" => FindingState.Open,
        "accepted" => FindingState.Accepted,
        "waived" => FindingState.Waived,
        "false-positive" => FindingState.FalsePositive,
        _ => throw new ArgumentException("Finding state must be open, accepted, waived, or false-positive."),
    };
    var store = new FindingStateStore(repository.Root);
    await store.MergeReviewAsync([identity], [], "quality-studio", cancellationToken);
    var updated = await store.SetAsync(
        request.Fingerprint, state, request.Author, request.Reason, request.ExpiresAt,
        request.ExpectedTimestamp, cancellationToken);
    logger.LogInformation(new EventId(1501, "FindingStateMutated"),
        "Set finding {FindingFingerprint} to {FindingState} for {FilePath} in repository {RepositoryId} by {Author}; ElapsedMilliseconds={ElapsedMilliseconds}",
        updated.Fingerprint, FindingStateStore.StateName(updated.State), relative, registration.Id, updated.Author, stopwatch.ElapsedMilliseconds);
    return Results.Ok(updated);
}

static async Task<IResult> FindingPolicy(HttpContext context, RepositoryRegistry registry,
    CancellationToken cancellationToken)
{
    var (_, repository) = ResolveRepository(context, registry);
    return Results.Ok(new
    {
        assessments = await new FindingAssessmentStore(repository.Root).ReadAsync(cancellationToken),
        suppressions = await new FindingSuppressionStore(repository.Root).ReadAsync(cancellationToken),
    });
}

static async Task<IResult> MutateFindingAssessment(HttpContext context, FindingAssessmentMutationRequest request,
    RepositoryRegistry registry, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(request.Path);
    var identity = await ReadFindingIdentity(repository, relative, request.Kind, request.Fingerprint, cancellationToken);
    FindingAssessmentStatus? assessment = request.Assessment is null ? null : request.Assessment switch
    {
        "unassessed" => FindingAssessmentStatus.Unassessed,
        "confirmed" => FindingAssessmentStatus.Confirmed,
        "dismissed" => FindingAssessmentStatus.Dismissed,
        "disputed" => FindingAssessmentStatus.Disputed,
        _ => throw new ArgumentException("Assessment must be unassessed, confirmed, dismissed, or disputed."),
    };
    FindingResolutionStatus? resolution = request.Resolution is null ? null : request.Resolution switch
    {
        "open" => FindingResolutionStatus.Open,
        "planned" => FindingResolutionStatus.Planned,
        "fixed" => FindingResolutionStatus.Fixed,
        "risk-accepted" => FindingResolutionStatus.RiskAccepted,
        "obsolete" => FindingResolutionStatus.Obsolete,
        "fixed-by-absence" => FindingResolutionStatus.FixedByAbsence,
        _ => throw new ArgumentException("Resolution must be open, planned, fixed, risk-accepted, obsolete, or fixed-by-absence."),
    };
    var updated = await new FindingAssessmentStore(repository.Root).AppendAsync(identity, assessment, resolution,
        request.Actor, request.Reason, request.ExpectedRevision, request.ReviewRunId, request.OperationRunId,
        request.TaskKey, cancellationToken);
    logger.LogInformation(new EventId(1502, "FindingAssessmentMutated"),
        "Updated assessment axes for finding {FindingFingerprint} in repository {RepositoryId} by {Actor}; Revision={Revision}; ElapsedMilliseconds={ElapsedMilliseconds}",
        updated.Fingerprint, registration.Id, updated.Actor, updated.Revision, stopwatch.ElapsedMilliseconds);
    return Results.Ok(updated);
}

static async Task<IResult> SuppressExactFinding(HttpContext context, ExactFindingSuppressionRequest request,
    RepositoryRegistry registry, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(request.Path);
    await ReadFindingIdentity(repository, relative, request.Kind, request.Fingerprint, cancellationToken);
    var rule = new FindingSuppressionRule(
        "exact-" + request.Fingerprint[7..], true, new FindingSuppressionMatch(Fingerprint: request.Fingerprint),
        "suppress", request.Reason, request.Author, DateTimeOffset.UtcNow, request.ExpiresAt);
    var updated = await new FindingSuppressionStore(repository.Root).SetAsync(rule, request.ExpectedRevision, cancellationToken);
    logger.LogInformation(new EventId(1503, "FindingSuppressed"),
        "Created exact suppression {SuppressionId} for finding {FindingFingerprint} in repository {RepositoryId}; Revision={Revision}",
        rule.Id, request.Fingerprint, registration.Id, updated.Revision);
    return Results.Ok(updated);
}

static async Task<IResult> PreviewFindingSuppression(HttpContext context, FindingSuppressionPreviewRequest request,
    RepositoryRegistry registry, CancellationToken cancellationToken)
{
    var (_, repository) = ResolveRepository(context, registry);
    var observations = await DiscoverFindingObservations(repository.Root, cancellationToken);
    var matches = new FindingSuppressionStore(repository.Root).Preview(request.Rule, observations);
    return Results.Ok(new { matchCount = matches.Count, matches });
}

static async Task<IResult> SaveFindingSuppression(HttpContext context, string id,
    FindingSuppressionMutationRequest request, RepositoryRegistry registry, CancellationToken cancellationToken)
{
    if (!string.Equals(id, request.Rule.Id, StringComparison.Ordinal))
        throw new ArgumentException("Suppression route id must match the rule id.");
    if (request.Rule.Match.Fingerprint is not null)
        throw new ArgumentException("Use the exact suppression endpoint for fingerprint-only rules.");
    var (_, repository) = ResolveRepository(context, registry);
    var observations = await DiscoverFindingObservations(repository.Root, cancellationToken);
    var store = new FindingSuppressionStore(repository.Root);
    var preview = store.Preview(request.Rule, observations);
    var updated = await store.SetAsync(request.Rule, request.ExpectedRevision, cancellationToken);
    return Results.Ok(new { document = updated, matchCount = preview.Count, matches = preview });
}

static async Task<FindingIdentityRecord> ReadFindingIdentity(RepositoryAccess repository, string relative,
    string kind, string fingerprint, CancellationToken cancellationToken)
{
    var metaPath = repository.FindMetaDocument(relative, kind);
    using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken));
    var finding = metadata.RootElement.GetProperty("findings").EnumerateArray().FirstOrDefault(candidate =>
        candidate.TryGetProperty("fingerprint", out var value) && value.GetString() == fingerprint);
    if (finding.ValueKind == JsonValueKind.Undefined)
        throw new KeyNotFoundException($"Finding '{fingerprint}' was not found in the selected review.");
    return new FindingIdentityRecord(
        fingerprint,
        finding.GetProperty("id").GetString()!,
        finding.GetProperty("locations")[0].GetProperty("path").GetString()!,
        finding.GetProperty("ruleId").GetString()!);
}

static async Task<IReadOnlyList<FindingObservation>> DiscoverFindingObservations(string repositoryRoot,
    CancellationToken cancellationToken)
{
    var observations = new Dictionary<string, FindingObservation>(StringComparer.Ordinal);
    var confinedEnumeration = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };
    foreach (var path in Directory.EnumerateFiles(repositoryRoot, "*.review-meta.*.json", confinedEnumeration))
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathConfinement.RejectReparseTraversal(repositoryRoot, path);
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var root = document.RootElement;
            var reviewKind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() ?? "" : "";
            if (!root.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array) continue;
            foreach (var finding in findings.EnumerateArray())
            {
                if (!finding.TryGetProperty("fingerprint", out var fingerprintValue) || fingerprintValue.GetString() is not { } fingerprint) continue;
                var sourceKind = finding.TryGetProperty("origin", out var origin) &&
                                 origin.TryGetProperty("kind", out var originKind)
                    ? originKind.GetString() ?? "agent"
                    : finding.TryGetProperty("source", out _) ? "deterministic" : "agent";
                observations[fingerprint] = new FindingObservation(
                    fingerprint,
                    finding.TryGetProperty("ruleId", out var ruleId) ? ruleId.GetString() ?? "" : "",
                    finding.GetProperty("locations")[0].GetProperty("path").GetString() ?? "",
                    reviewKind,
                    sourceKind,
                    finding.TryGetProperty("id", out var findingId) ? findingId.GetString() ?? "" : "",
                    finding.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "");
            }
        }
        catch (JsonException)
        {
            // Invalid sidecars are surfaced when opened; broad preview remains available for valid observations.
        }
    }
    return observations.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.FindingId, StringComparer.Ordinal).ToArray();
}

static async Task<IResult> MutateThread(HttpContext context, ThreadMutationRequest request,
    RepositoryRegistry registry, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    if (string.IsNullOrWhiteSpace(request.Body) && request.Status is null)
        throw new ArgumentException("A comment body or status change is required.");
    if (request.Body?.Length > 20000) throw new ArgumentException("A comment body cannot exceed 20,000 characters.");
    if (request.HumanName?.Length > 200) throw new ArgumentException("A reviewer name cannot exceed 200 characters.");
    if (request.ReplyTo?.Length > 200) throw new ArgumentException("A reply target cannot exceed 200 characters.");
    if (request.Status is not null && request.Status is not ("open" or "resolved"))
        throw new ArgumentException("Thread status must be open or resolved.");
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(request.Path);
    var metaPath = repository.FindMetaDocument(relative, request.Kind);
    var writeLock = ReviewThreadManager.GetWriteLock(metaPath);
    await writeLock.WaitAsync(cancellationToken);
    try
    {
    var root = JsonNode.Parse(await File.ReadAllTextAsync(metaPath, cancellationToken))!.AsObject();
    var threads = root["threads"] as JsonArray ?? [];
    root["threads"] = threads;
    JsonObject thread;
    if (string.IsNullOrWhiteSpace(request.ThreadId))
    {
        if (request.Line is null or < 1 || string.IsNullOrWhiteSpace(request.Body))
            throw new ArgumentException("A new thread requires a line and comment body.");
        var content = await File.ReadAllTextAsync(repository.ResolveFile(relative), cancellationToken);
        var lineCount = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Length;
        if (request.Line > lineCount) throw new ArgumentException($"Line {request.Line} is outside the file (1-{lineCount}).");
        var range = new FindingRange(new FindingPosition(request.Line.Value, 1), new FindingPosition(request.Line.Value, 1));
        var fingerprint = request.FindingFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 71 || !fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
            !fingerprint[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{relative}\0{request.Line}\0{request.Body}")));
        thread = new JsonObject
        {
            ["id"] = $"thread-{Guid.NewGuid():N}",
            ["anchor"] = new JsonObject
            {
                ["path"] = relative, ["fingerprint"] = fingerprint,
                ["contextHash"] = ReviewThreadManager.ComputeContextHash(content, range),
                ["lastKnownRange"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = request.Line, ["column"] = 1 },
                    ["end"] = new JsonObject { ["line"] = request.Line, ["column"] = 1 },
                },
            },
            ["status"] = request.Status ?? "open", ["anchorState"] = "anchored", ["entries"] = new JsonArray(),
        };
        threads.Add(thread);
    }
    else
    {
        thread = threads.OfType<JsonObject>().SingleOrDefault(candidate => candidate["id"]?.GetValue<string>() == request.ThreadId)
            ?? throw new KeyNotFoundException($"Review thread '{request.ThreadId}' was not found.");
    }
    if (!string.IsNullOrWhiteSpace(request.Body))
    {
        var entry = new JsonObject
        {
            ["id"] = $"entry-{Guid.NewGuid():N}",
            ["author"] = new JsonObject { ["kind"] = "human", ["name"] = string.IsNullOrWhiteSpace(request.HumanName) ? "Reviewer" : request.HumanName.Trim() },
            ["createdAt"] = DateTime.UtcNow.ToString("O"), ["body"] = request.Body.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(request.ReplyTo)) entry["replyTo"] = request.ReplyTo;
        thread["entries"]!.AsArray().Add(entry);
    }
    if (request.Status is not null) thread["status"] = request.Status;
    var temporary = metaPath + ".tmp-" + Guid.NewGuid().ToString("N");
    await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
        new UTF8Encoding(false), cancellationToken);
    File.Move(temporary, metaPath, true);
    logger.LogInformation(new EventId(1500, "ReviewThreadMutated"),
        "Mutated review thread {ThreadId} for {FilePath} in repository {RepositoryId}; Status={Status}, HasEntry={HasEntry}, ElapsedMilliseconds={ElapsedMilliseconds}",
        thread["id"]!.GetValue<string>(), relative, registration.Id, thread["status"]!.GetValue<string>(), !string.IsNullOrWhiteSpace(request.Body), stopwatch.ElapsedMilliseconds);
    return Results.Ok(thread);
    }
    finally
    {
        writeLock.Release();
    }
}

static (string Encoding, string Content) DecodeFileContent(byte[] bytes)
{
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
    {
        return ("utf-8-bom", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    try
    {
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return ("utf-8", strictUtf8.GetString(bytes));
    }
    catch (DecoderFallbackException)
    {
        // Latin-1 maps every byte 0-255 to the code point of the same value, so it can
        // never throw and never collapses bytes into U+FFFD the way a lossy UTF-8 decode would.
        return ("other", Encoding.Latin1.GetString(bytes));
    }
}

static string DetectLineEnding(string content)
{
    var sawCrlf = false;
    var sawLoneLf = false;
    for (var i = 0; i < content.Length; i++)
    {
        if (content[i] != '\n') continue;
        if (i > 0 && content[i - 1] == '\r') sawCrlf = true; else sawLoneLf = true;
    }

    return sawCrlf && sawLoneLf ? "mixed" : sawCrlf ? "crlf" : "lf";
}

static IResult Inputs(HttpContext context, RepositoryRegistry registry, InputResolver resolver, ILogger<Program> logger)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var globalDirectory = registration.GlobalInputsDirectory;
    var kinds = registration.EnabledReviewKinds.ToDictionary(
        kind => kind,
        kind => resolver.Resolve(repository.Root, kind, ReviewLevel.File,
            globalDirectory, registration.InputBudgetCharacters),
        StringComparer.Ordinal);
    logger.LogInformation(new EventId(1102, "InputsResolved"),
        "Resolved review inputs for {KindCount} kinds in repository {RepositoryId} in {ElapsedMilliseconds} ms",
        kinds.Count, registration.Id, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new { level = "file", kinds });
}

static IResult Guidelines(HttpContext context, RepositoryRegistry registry, GuidelineStore store)
{
    var (_, repository) = ResolveRepository(context, registry);
    var guidelines = store.List(repository.Root);
    return Results.Ok(new
    {
        guidelines,
        catalogue = GuidelineStore.Catalogue,
        traces = BuildGuidelineTraces(repository.Root, guidelines.Select(value => value.Id)),
    });
}

static IResult CreateGuideline(HttpContext context, GuidelineDraft request, RepositoryRegistry registry, GuidelineStore store)
{
    var (_, repository) = ResolveRepository(context, registry);
    var created = store.Create(repository.Root, request);
    return Results.Created($"{context.Request.Path}/{Uri.EscapeDataString(created.Id)}", created);
}

static IResult UpdateGuideline(HttpContext context, string guidelineId, GuidelineDraft request,
    RepositoryRegistry registry, GuidelineStore store)
{
    var (_, repository) = ResolveRepository(context, registry);
    return Results.Ok(store.Update(repository.Root, guidelineId, request));
}

static IResult DeleteGuideline(HttpContext context, string guidelineId, RepositoryRegistry registry, GuidelineStore store)
{
    var (_, repository) = ResolveRepository(context, registry);
    store.Delete(repository.Root, guidelineId);
    return Results.NoContent();
}

static IResult InstallGuideline(HttpContext context, string catalogueId, RepositoryRegistry registry, GuidelineStore store)
{
    var (_, repository) = ResolveRepository(context, registry);
    var installed = store.Install(repository.Root, catalogueId);
    return Results.Created($"{context.Request.PathBase}/api/guidelines/{Uri.EscapeDataString(installed.Id)}", installed);
}

static async Task<IResult> GuidelineImpact(HttpContext context, GuidelineImpactRequest request,
    RepositoryRegistry registry, GuidelineImpactAnalyzer analyzer, CancellationToken cancellationToken)
{
    var (registration, repository) = ResolveRepository(context, registry);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    var configured = request with
    {
        GlobalInputsDirectory = globalDirectory,
        InputBudgetCharacters = registration.InputBudgetCharacters,
    };
    return Results.Ok(await analyzer.AnalyzeAsync(repository.Root, configured, cancellationToken));
}

static IReadOnlyList<GuidelineTraceResponse> BuildGuidelineTraces(string repositoryRoot, IEnumerable<string> guidelineIds)
{
    var ids = guidelineIds.ToHashSet(StringComparer.Ordinal);
    var findings = ids.ToDictionary(id => id, _ => new List<GuidelineTraceFindingResponse>(), StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(repositoryRoot, "*.json", SearchOption.AllDirectories)
                 .Where(path => path.Contains(".review-meta.", StringComparison.Ordinal)))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("findings", out var values) || values.ValueKind != JsonValueKind.Array) continue;
        var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() ?? "code" : "code";
        var unitPath = root.TryGetProperty("unit", out var unit) && unit.TryGetProperty("path", out var unitPathValue)
            ? unitPathValue.GetString() ?? string.Empty : string.Empty;
        foreach (var finding in values.EnumerateArray())
        {
            if (!finding.TryGetProperty("ruleId", out var ruleIdValue) || ruleIdValue.GetString() is not { } ruleId ||
                !findings.TryGetValue(ruleId, out var target)) continue;
            target.Add(new GuidelineTraceFindingResponse(
                finding.GetProperty("id").GetString()!, ruleId, finding.GetProperty("title").GetString()!,
                finding.GetProperty("severity").GetString()!, kind, unitPath,
                Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')));
        }
    }
    return findings.Select(pair => new GuidelineTraceResponse(pair.Key, pair.Value.Count, pair.Value)).ToArray();
}

static async Task<IResult> Scan(HttpContext context, RepositoryRegistry registry, StalenessEvaluator evaluator,
    RepositoryHierarchyCache hierarchyCache, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    _ = hierarchyCache.Get(repository.Root, globalInputsDirectory: globalDirectory,
        inputBudgetCharacters: registration.InputBudgetCharacters);
    var report = await evaluator.ScanAsync(repository.Root, new StalenessEvaluatorOptions
    {
        GlobalInputsDirectory = globalDirectory,
        InputBudgetCharacters = registration.InputBudgetCharacters,
    }, cancellationToken);
    logger.LogInformation(new EventId(1200, "ScanCompleted"),
        "Scanned repository {RepositoryId} with {FileCount} files in {ElapsedMilliseconds} ms",
        registration.Id, report.Files.Count, stopwatch.ElapsedMilliseconds);
    return Results.Ok(report);
}

static async Task<IResult> SecurityScan(HttpContext context, RepositoryRegistry registry, GitleaksSecurityScanner scanner,
    ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var result = await scanner.ScanAsync(new SecurityScanRequest(repository.Root, PersistMetadata: false), cancellationToken);
    logger.LogInformation(new EventId(1201, "SecurityScanCompleted"),
        "Scanned repository {RepositoryId} for secrets with verdict {Verdict} in {ElapsedMilliseconds} ms",
        registration.Id, result.Report.Verdict.ToString().ToLowerInvariant(), stopwatch.ElapsedMilliseconds);
    return Results.Ok(Map(result));
}

static async Task<IResult> AttackCoverage(
    HttpContext context,
    string? path,
    bool? recheck,
    RepositoryRegistry registry,
    BoundaryInventorySensor boundaries,
    AttackCatalogueResolver catalogues,
    AttackCoverageService coverage,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var scope = string.IsNullOrWhiteSpace(path) ? "." : repository.NormalizeRelativePath(path);
    var request = scope == "."
        ? new SensorScanRequest(repository.Root, PersistMetadata: false)
        : new SensorScanRequest(repository.Root, SensorScope.Path, scope, PersistMetadata: false);
    var inventory = await boundaries.InventoryAsync(request, cancellationToken);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    var catalogue = catalogues.Resolve(repository.Root, globalDirectory);
    var matrix = await coverage.BuildAsync(
        repository.Root, inventory, catalogue, scope, recheckDeterministic: recheck == true, cancellationToken);
    logger.LogInformation(new EventId(1203, "AttackCoverageLoaded"),
        "Loaded {CellCount} attack coverage cells for repository {RepositoryId} scope {Scope}; Stale={StaleCount}, Deferred={DeferredCount}, Disagreement={DisagreementCount}, ElapsedMilliseconds={ElapsedMilliseconds}",
        matrix.CellCount, registration.Id, scope, matrix.StaleCount, matrix.NotYetCheckedCount,
        matrix.DisagreementCount, stopwatch.ElapsedMilliseconds);
    return Results.Ok(matrix);
}

static async Task<IResult> RecordAttackJudgement(
    HttpContext context,
    string? path,
    AttackJudgementSubmission request,
    RepositoryRegistry registry,
    BoundaryInventorySensor boundaries,
    AttackCatalogueResolver catalogues,
    AttackCoverageService coverage,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var (registration, repository) = ResolveRepository(context, registry);
    var scope = string.IsNullOrWhiteSpace(path) ? "." : repository.NormalizeRelativePath(path);
    var sensorRequest = scope == "."
        ? new SensorScanRequest(repository.Root, PersistMetadata: false)
        : new SensorScanRequest(repository.Root, SensorScope.Path, scope, PersistMetadata: false);
    var inventory = await boundaries.InventoryAsync(sensorRequest, cancellationToken);
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
    var catalogue = catalogues.Resolve(repository.Root, globalDirectory);
    var observation = await coverage.RecordAsync(
        repository.Root, inventory, catalogue, request, cancellationToken);
    logger.LogInformation(new EventId(1204, "AttackJudgementRecorded"),
        "Recorded {Verdict} judgement for boundary {BoundaryId}, attack {AttackId}, repository {RepositoryId}, assessment {AssessmentId}",
        observation.Verdict, observation.BoundaryId, observation.AttackId, registration.Id,
        observation.AssessmentId);
    return Results.Created(
        $"/api/repos/{registration.Id}/security/attack-coverage?path={Uri.EscapeDataString(scope)}",
        observation);
}

static async Task<IResult> Sensors(HttpContext context, RepositoryRegistry repositories, SensorRegistry sensors,
    CancellationToken cancellationToken)
{
    var registration = repositories.Get(RouteRepositoryId(context));
    var configured = (registration.Sensors ?? Array.Empty<RepositorySensorConfiguration>())
        .ToDictionary(sensor => sensor.Id, StringComparer.OrdinalIgnoreCase);
    var descriptors = new List<object>();
    foreach (var sensor in sensors.List())
    {
        var availability = await sensor.ProbeAvailabilityAsync(cancellationToken);
        configured.TryGetValue(sensor.Id, out var repositoryConfiguration);
        descriptors.Add(new
        {
            sensor.Id,
            sensor.Version,
            Scopes = sensor.SupportedScopes.Select(scope => scope.ToString().ToLowerInvariant()).ToArray(),
            Enabled = repositoryConfiguration?.Enabled == true,
            Configuration = repositoryConfiguration?.Configuration,
            availability.Available,
            availability.UnavailableReason,
            availability.ToolVersions,
        });
    }

    return Results.Ok(new { sensors = descriptors });
}

static async Task<IResult> SensorScan(HttpContext context, string id, string? path,
    RepositoryRegistry repositories, SensorRegistry sensors, ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var registration = repositories.Get(RouteRepositoryId(context));
    var sensor = sensors.Get(id);
    var repositoryConfiguration = (registration.Sensors ?? Array.Empty<RepositorySensorConfiguration>())
        .FirstOrDefault(configuration => string.Equals(configuration.Id, id, StringComparison.OrdinalIgnoreCase));
    if (repositoryConfiguration is null || !repositoryConfiguration.Enabled)
    {
        throw new RepositoryRegistryValidationException($"Sensor '{id}' is not enabled for repository '{registration.Id}'.");
    }

    var scope = string.IsNullOrWhiteSpace(path) ? SensorScope.Repository : SensorScope.Path;
    var result = await sensor.RunAsync(new SensorScanRequest(
        registration.RootPath,
        scope,
        path,
        repositoryConfiguration.Configuration), cancellationToken);
    logger.LogInformation(new EventId(1202, "SensorScanCompleted"),
        "Ran sensor {SensorId} for repository {RepositoryId}; Available={Available}, Findings={FindingCount}, ElapsedMilliseconds={ElapsedMilliseconds}",
        sensor.Id, registration.Id, result.Available, result.Findings.Count, stopwatch.ElapsedMilliseconds);
    return Results.Ok(result);
}

static async Task<IResult> Usage(HttpContext context, DateTimeOffset? since, string? kind,
    RepositoryRegistry registry, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var report = await UsageLedger.QueryAsync(repository.Root, since, kind, cancellationToken: cancellationToken);
    logger.LogInformation(new EventId(1400, "UsageLoaded"),
        "Loaded {UsageRunCount} usage entries for repository {RepositoryId} in {ElapsedMilliseconds} ms",
        report.Runs, registration.Id, stopwatch.ElapsedMilliseconds);
    return Results.Ok(report);
}

static async Task<IResult> Report(HttpContext context, string? format,
    RepositoryRegistry registry, SensorRegistry sensorRegistry, ApiSecurity security,
    QualityReportBuilder builder, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var requestedId = RouteRepositoryId(context);
    var registrations = requestedId is null
        ? registry.List().Where(repository => security.Identity(context).CanAccess(repository.Id)).ToArray()
        : [registry.Get(requestedId)];
    if (registrations.Length == 0) throw new KeyNotFoundException("No accessible repositories were found.");

    var repositories = registrations.Select(registration => new QualityReportRepository(
        registration.Id,
        registration.DisplayName,
        registration.RootPath,
        registration.EnabledReviewKinds,
        (registration.Sensors ?? []).Select(configuration =>
        {
            var sensor = sensorRegistry.Get(configuration.Id);
            return new QualityReportSensor(sensor.Id, sensor.Version, configuration.Enabled);
        }).ToArray(),
        registration.GlobalInputsDirectory,
        registration.InputBudgetCharacters)).ToArray();
    var report = await builder.BuildAsync(repositories, cancellationToken);
    var selectedFormat = string.IsNullOrWhiteSpace(format)
        ? QualityReportFormat.Json
        : QualityReportRenderer.ParseFormat(format);
    var rendered = QualityReportRenderer.Render(report, selectedFormat);
    logger.LogInformation(new EventId(1600, "QualityReportGenerated"),
        "Generated {ReportFormat} quality report for {RepositoryCount} repositories in {ElapsedMilliseconds} ms",
        selectedFormat, repositories.Length, stopwatch.ElapsedMilliseconds);
    return Results.Text(rendered, QualityReportRenderer.ContentType(selectedFormat), Encoding.UTF8);
}

static IResult Quotas(QuotaService quotas, ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var report = quotas.GetWithBackgroundRefresh(cancellationToken);
    logger.LogInformation(new EventId(1401, "QuotasLoaded"),
        "Loaded {QuotaProviderCount} quota providers in {ElapsedMilliseconds} ms",
        report.Snapshots.Count, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new
    {
        report.At,
        report.TtlSeconds,
        Providers = report.Snapshots.Select(snapshot => new
        {
            Provider = snapshot.CliType,
            snapshot.Plan,
            snapshot.FetchedAt,
            snapshot.Source,
            snapshot.Error,
            Windows = snapshot.Windows.Select(window => new
            {
                window.Label,
                window.UsedPct,
                RemainingPct = window.UsedPct.HasValue ? (double?)Math.Max(0d, 100d - window.UsedPct.Value) : null,
                window.Used,
                window.Limit,
                window.Unit,
                window.ResetAt,
                window.ResetLabel,
            }),
        }),
    });
}

static async Task<IResult> StartReview(
    HttpContext context,
    StartReviewRequest request,
    RepositoryRegistry registry,
    ReviewJobService jobs,
    CancellationToken cancellationToken)
{
    var repository = registry.Get(RouteRepositoryId(context));
    var run = await jobs.EnqueueAsync(repository.Id, request, cancellationToken);
    var basePath = RouteRepositoryId(context) is null ? "/api/review/runs" : $"/api/repos/{Uri.EscapeDataString(repository.Id)}/review/runs";
    return Results.Accepted($"{basePath}/{run.Id}", run);
}

static async Task<IResult> EstimateReview(
    HttpContext context,
    StartReviewRequest request,
    RepositoryRegistry registry,
    ReviewJobService jobs,
    CancellationToken cancellationToken)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(await jobs.EstimateAsync(repository.Id, request, cancellationToken));
}

static IResult ReviewRuns(HttpContext context, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(new { runs = jobs.List(repository.Id) });
}

static IResult ReviewHistory(HttpContext context, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(new { runs = jobs.History(repository.Id) });
}

static IResult ReviewHistoryRun(HttpContext context, string id, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(jobs.History(repository.Id, id));
}

static IResult ReviewRun(HttpContext context, string id, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(jobs.Get(repository.Id, id));
}

static IResult CancelReview(HttpContext context, string id, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(jobs.Cancel(repository.Id, id));
}

static IResult PauseReview(HttpContext context, string id, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(jobs.Pause(repository.Id, id));
}

static IResult ResumeReview(
    HttpContext context, string id, ResumeReviewRequest? request, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    return Results.Ok(jobs.Resume(repository.Id, id, request));
}

static IResult HandoverConfiguration(HttpContext context, RepositoryRegistry registry, AgentStudioTaskOptions options)
{
    registry.Get(RouteRepositoryId(context));
    return Results.Ok(new HandoverConfigurationResponse(options.IsTargetConfigured, options.DryRun, options.Project));
}

static async Task<IResult> Handover(
    HttpContext context,
    HandoverRequest request,
    RepositoryRegistry registry,
    AgentStudioTaskClient client,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var filePath = repository.NormalizeRelativePath(request.FilePath);
    repository.ResolveFile(filePath);
    var metaReferencePath = request.MetaReference.Split('#', 2)[0];
    if (!string.IsNullOrWhiteSpace(metaReferencePath)) repository.NormalizeRelativePath(metaReferencePath);
    var result = await client.CreateTaskAsync(new FindingTaskTemplate(
        request.FindingSummary,
        filePath,
        request.FindingText,
        request.ReviewKind,
        request.MetaReference), cancellationToken);
    logger.LogInformation(new EventId(1300, "FindingHandedOver"),
        "Handed over finding for {FilePath} and {ReviewKind} in repository {RepositoryId}; DryRun={DryRun}, TaskId={TaskId}, ElapsedMilliseconds={ElapsedMilliseconds}",
        filePath, request.ReviewKind, registration.Id, result.DryRun, result.TaskId, stopwatch.ElapsedMilliseconds);
    return Results.Ok(result);
}

static async Task<IResult> ImportFromAgentStudio(
    RepositoryRegistry registry,
    AgentStudioTaskClient client,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    // Fetch the full project list before touching the registry: if Agent Studio is offline or
    // unconfigured, this throws and the exception middleware returns a clear error with zero writes.
    var projects = await client.GetProjectsAsync(cancellationToken);
    var knownPaths = registry.List(includeArchived: true)
        .Select(repository => repository.RootPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var results = new List<AgentStudioImportResultResponse>();
    foreach (var project in projects)
    {
        if (project.Archived)
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(project.RepositoryPath))
        {
            results.Add(new AgentStudioImportResultResponse(
                project.Id, project.DisplayName, null, "failed", null, "No repository path configured in Agent Studio."));
            continue;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(project.RepositoryPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            results.Add(new AgentStudioImportResultResponse(
                project.Id, project.DisplayName, project.RepositoryPath, "failed", null, "Repository path is not a valid local path."));
            continue;
        }

        if (!Directory.Exists(normalizedPath))
        {
            results.Add(new AgentStudioImportResultResponse(
                project.Id, project.DisplayName, normalizedPath, "failed", null, "Repository path does not exist."));
            continue;
        }

        if (knownPaths.Contains(normalizedPath))
        {
            results.Add(new AgentStudioImportResultResponse(
                project.Id, project.DisplayName, normalizedPath, "skipped", null, "Already registered."));
            continue;
        }

        try
        {
            var created = await registry.CreateAsync(new RepositoryRegistrationRequest(
                string.IsNullOrWhiteSpace(project.ShortCode) ? null : project.ShortCode,
                project.DisplayName,
                normalizedPath,
                null,
                null,
                null), cancellationToken);
            knownPaths.Add(created.RootPath);
            results.Add(new AgentStudioImportResultResponse(
                project.Id, project.DisplayName, created.RootPath, "imported", created.Id, null));
        }
        catch (RepositoryRegistryValidationException exception)
        {
            results.Add(new AgentStudioImportResultResponse(
                project.Id, project.DisplayName, normalizedPath, "failed", null, exception.Message));
        }
    }

    var imported = results.Count(result => result.Status == "imported");
    var skipped = results.Count(result => result.Status == "skipped");
    var failed = results.Count(result => result.Status == "failed");
    logger.LogInformation(new EventId(1404, "RepositoriesImportedFromAgentStudio"),
        "Imported {ImportedCount} repositories from Agent Studio ({SkippedCount} skipped, {FailedCount} failed, {ProjectCount} projects seen) in {ElapsedMilliseconds} ms",
        imported, skipped, failed, projects.Count, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new AgentStudioImportResponse(results, imported, skipped, failed));
}

static (RepositoryRegistration Registration, RepositoryAccess Access) ResolveRepository(
    HttpContext context, RepositoryRegistry registry)
{
    var id = RouteRepositoryId(context);
    var registration = registry.Get(id);
    return (registration, registry.Access(registration.Id));
}

static string? RouteRepositoryId(HttpContext context) =>
    context.Request.RouteValues.TryGetValue("repoId", out var routeId) ? routeId?.ToString() : null;

static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
{
    foreach (var root in roots)
    {
        yield return root;
        foreach (var descendant in Flatten(root.Children))
        {
            yield return descendant;
        }
    }
}

static SecurityScanResponse Map(SecurityScanResult result) => new(
    result.Report.Verdict.ToString().ToLowerInvariant(),
    result.Report.Available,
    result.Report.Scanner,
    result.Report.Version,
    result.Report.Mode,
    result.Report.Range,
    result.Report.ConfigPath,
    result.Report.BaselinePath,
    result.Report.ScannedAt ?? result.Provenance.ScannedAt,
    result.Report.FilesScanned,
    result.Report.NewFindings,
    result.Report.AcceptedFindings,
    result.Report.BlockFindings,
    result.Report.WarnFindings,
    result.Report.CleanFiles,
    result.Report.UnavailableReason,
    new SecurityScanProvenanceResponse(
        result.Provenance.Scanner,
        result.Provenance.Version,
        result.Provenance.Mode,
        result.Provenance.Range,
        result.Provenance.ConfigPath,
        result.Provenance.BaselinePath,
        result.Provenance.ScannedAt),
    new SecurityScanCountsResponse(
        result.Counts.FilesScanned,
        result.Counts.NewFindings,
        result.Counts.AcceptedFindings,
        result.Counts.BlockFindings,
        result.Counts.WarnFindings,
        result.Counts.CleanFiles),
    result.Findings.Select(finding => new SecurityFindingResponse(
        finding.Id,
        finding.Aspect,
        finding.Severity.ToString().ToLowerInvariant(),
        finding.Title,
        finding.Description,
        finding.Recommendation,
        finding.Locations.Select(location => new SecurityFindingLocationResponse(
            location.Path,
            new SecurityFindingRangeResponse(
                new SecurityFindingPositionResponse(location.Range!.Start.Line, location.Range.Start.Column),
                new SecurityFindingPositionResponse(location.Range.End.Line, location.Range.End.Column)))).ToArray(),
        finding.Fingerprint,
        finding.RuleId,
        finding.Evidence,
        finding.Path,
        finding.Accepted)).ToArray());

public partial class Program;
