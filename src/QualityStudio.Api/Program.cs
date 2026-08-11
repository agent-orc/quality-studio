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
builder.Services.AddSingleton<LocalMutationProtection>();
builder.Services.AddSingleton<AnalyzerProfileRegistry>();
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
ApiSecurity.ValidateLocalBindings(corsOptions, builder.Configuration);
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
        ReviewContentLimitException => (StatusCodes.Status413PayloadTooLarge, "Repository content exceeds configured limits"),
        JsonException => (StatusCodes.Status422UnprocessableEntity, "Repository security metadata is invalid"),
        SecurityScannerUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Security scanner unavailable"),
        HttpRequestException => (StatusCodes.Status502BadGateway, "Agent Studio request failed"),
        InvalidOperationException => (StatusCodes.Status503ServiceUnavailable, "Agent Studio target unavailable"),
        FindingStateConflictException => (StatusCodes.Status409Conflict, "Finding state changed"),
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

    var authentication = apiSecurity.AuthenticateDecision(context);
    var identity = authentication.Identity;
    if (identity is null)
    {
        AuditAuthorization(context, authentication.KeyId, RequiredApiRole(context), null,
            "denied", authentication.Reason, StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required").ExecuteAsync(context);
        return;
    }
    apiSecurity.SetIdentity(context, identity);
    var requiredRole = RequiredApiRole(context);
    if (!ApiRoles.All.Contains(requiredRole))
    {
        AuditAuthorization(context, identity.KeyId, requiredRole, RouteRepositoryId(context),
            "denied", "missing-route-policy", StatusCodes.Status403Forbidden);
        await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "The API route has no authorization policy").ExecuteAsync(context);
        return;
    }

    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
        HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
    {
        if (apiSecurity.IsLocal)
        {
            var localMutations = context.RequestServices.GetRequiredService<LocalMutationProtection>();
            if (!apiSecurity.IsAllowedLocalOrigin(context) || !localMutations.Validate(context))
            {
                AuditAuthorization(context, identity.KeyId, requiredRole, RouteRepositoryId(context),
                    "denied", "local-csrf", StatusCodes.Status403Forbidden);
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Local mutations require an allowed Origin and anti-CSRF token").ExecuteAsync(context);
                return;
            }
        }
        if (!apiSecurity.IsMutationClientHeaderValid(context, identity))
        {
            AuditAuthorization(context, identity.KeyId, requiredRole, RouteRepositoryId(context),
                "denied", "client-id-mismatch", StatusCodes.Status401Unauthorized);
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: "A matching X-Client-Id is required for mutations").ExecuteAsync(context);
            return;
        }
    }

    var path = context.Request.Path.Value ?? string.Empty;
    var repositoryId = RouteRepositoryId(context);
    var isRepositoryCollection = string.Equals(path, "/api/repos", StringComparison.OrdinalIgnoreCase);
    var isReportCollection = string.Equals(path, "/api/report", StringComparison.OrdinalIgnoreCase);
    var isRepositoryAdministration = string.Equals(requiredRole, ApiRoles.RepositoryAdmin, StringComparison.Ordinal);
    if (isRepositoryAdministration)
    {
        if (!identity.CanRegisterRepositories)
        {
            AuditAuthorization(context, identity.KeyId, ApiRoles.RepositoryAdmin, repositoryId,
                "denied", "missing-role", StatusCodes.Status403Forbidden);
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Repository registration is not permitted")
                .ExecuteAsync(context);
            return;
        }
    }
    else if (repositoryId is not null && !identity.CanAccess(repositoryId))
    {
        AuditAuthorization(context, identity.KeyId, requiredRole, repositoryId,
            "denied", "repository-scope", StatusCodes.Status404NotFound);
        await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Repository not found").ExecuteAsync(context);
        return;
    }
    else if (repositoryId is null && !isRepositoryCollection && !isReportCollection &&
             !string.Equals(path, "/api/quotas", StringComparison.OrdinalIgnoreCase) &&
             !identity.CanAccess(RepositoryRegistry.DefaultRepositoryId))
    {
        AuditAuthorization(context, identity.KeyId, requiredRole, RepositoryRegistry.DefaultRepositoryId,
            "denied", "repository-scope", StatusCodes.Status404NotFound);
        await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Repository not found").ExecuteAsync(context);
        return;
    }

    if (!identity.HasRole(requiredRole))
    {
        AuditAuthorization(context, identity.KeyId, requiredRole,
            repositoryId ?? RepositoryRegistry.DefaultRepositoryId,
            "denied", "missing-role", StatusCodes.Status403Forbidden);
        await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "The API client role does not permit this action").ExecuteAsync(context);
        return;
    }

    AuditAuthorization(context, identity.KeyId, requiredRole,
        repositoryId ?? RepositoryRegistry.DefaultRepositoryId,
        "allowed", "authorized", null);
    await next();
    AuditAuthorization(context, identity.KeyId, requiredRole,
        repositoryId ?? RepositoryRegistry.DefaultRepositoryId,
        "result", "completed", context.Response.StatusCode);
});
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "QualityStudio.Api" }));

app.MapGet("/api/security/csrf", (HttpContext context, ApiSecurity security, LocalMutationProtection protection) =>
{
    if (!security.IsLocal) return Results.NotFound();
    if (!security.IsAllowedLocalOrigin(context))
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Origin is not allowed");
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Vary = "Origin";
    return Results.Ok(protection.Issue());
}).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));

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
}).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));

app.MapPost("/api/repos", async (RepositoryRegistrationRequest request, RepositoryRegistry registry,
    RepositorySnapshotPrewarmer prewarmer, CancellationToken cancellationToken) =>
{
    var created = await registry.CreateAsync(request, cancellationToken);
    prewarmer.Queue(created);
    return Results.Created($"/api/repos/{created.Id}", created);
}).WithMetadata(new ApiRoleMetadata(ApiRoles.RepositoryAdmin));

app.MapPost("/api/repos/import-from-agent-studio", ImportFromAgentStudio)
    .WithMetadata(new ApiRoleMetadata(ApiRoles.RepositoryAdmin));

app.MapPut("/api/repos/{repoId}", async (string repoId, RepositoryRegistrationRequest request,
    RepositoryRegistry registry, RepositorySnapshotPrewarmer prewarmer, CancellationToken cancellationToken) =>
{
    var updated = await registry.UpdateAsync(repoId, request, cancellationToken);
    prewarmer.Queue(updated);
    return Results.Ok(updated);
}).WithMetadata(new ApiRoleMetadata(ApiRoles.RepositoryAdmin));

app.MapDelete("/api/repos/{repoId}", async (string repoId, RepositoryRegistry registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.ArchiveAsync(repoId, cancellationToken)))
    .WithMetadata(new ApiRoleMetadata(ApiRoles.RepositoryAdmin));

app.MapGet("/api/tree", Tree).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/tree", Tree).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/risk", Risk).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/risk", Risk).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/project", ProjectDashboard).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/project", ProjectDashboard).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/file", FileContent).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/file", FileContent).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/review-meta/diagnostics", ReviewMetaDiagnostics).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/review-meta/diagnostics", ReviewMetaDiagnostics).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/inputs", Inputs).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/inputs", Inputs).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/guidelines", Guidelines).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/guidelines", Guidelines).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapPost("/api/guidelines", CreateGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/repos/{repoId}/guidelines", CreateGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPut("/api/guidelines/{guidelineId}", UpdateGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPut("/api/repos/{repoId}/guidelines/{guidelineId}", UpdateGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapDelete("/api/guidelines/{guidelineId}", DeleteGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapDelete("/api/repos/{repoId}/guidelines/{guidelineId}", DeleteGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/guidelines/catalog/{catalogueId}/install", InstallGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/repos/{repoId}/guidelines/catalog/{catalogueId}/install", InstallGuideline).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/guidelines/impact", GuidelineImpact).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/repos/{repoId}/guidelines/impact", GuidelineImpact).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapGet("/api/scan", Scan).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/repos/{repoId}/scan", Scan).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/security/scan", SecurityScan).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/repos/{repoId}/security/scan", SecurityScan).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/security/attack-coverage", AttackCoverage).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/repos/{repoId}/security/attack-coverage", AttackCoverage).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapPost("/api/security/attack-coverage/judgements", RecordAttackJudgement).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapPost("/api/repos/{repoId}/security/attack-coverage/judgements", RecordAttackJudgement).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/sensors", Sensors).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/sensors", Sensors).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapPost("/api/sensors/{id}/scan", SensorScan).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapPost("/api/repos/{repoId}/sensors/{id}/scan", SensorScan).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.SensorExecute));
app.MapGet("/api/usage", Usage).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/usage", Usage).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/report", Report).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/report", Report).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/quotas", Quotas).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/models", (ReviewModelCatalog catalog) => Results.Ok(catalog.Snapshot))
    .WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/scope/rules", ScopeRules).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/scope/rules", ScopeRules).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapPost("/api/scope/rules/preview", PreviewScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/repos/{repoId}/scope/rules/preview", PreviewScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/scope/rules", AddScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/repos/{repoId}/scope/rules", AddScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPut("/api/scope/rules/{index:int}", UpdateScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPut("/api/repos/{repoId}/scope/rules/{index:int}", UpdateScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapDelete("/api/scope/rules/{index:int}", DeleteScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapDelete("/api/repos/{repoId}/scope/rules/{index:int}", DeleteScopeRule).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));

app.MapPost("/api/review", StartReview).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/repos/{repoId}/review", StartReview).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/review/estimate", EstimateReview).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/repos/{repoId}/review/estimate", EstimateReview).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapGet("/api/review/runs", ReviewRuns).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/review/runs", ReviewRuns).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/review/runs/{id}", ReviewRun).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/review/runs/{id}", ReviewRun).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapPost("/api/review/runs/{id}/pause", PauseReview).WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/repos/{repoId}/review/runs/{id}/pause", PauseReview).WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/review/runs/{id}/resume", ResumeReview).WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapPost("/api/repos/{repoId}/review/runs/{id}/resume", ResumeReview).WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapDelete("/api/review/runs/{id}", CancelReview).WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));
app.MapDelete("/api/repos/{repoId}/review/runs/{id}", CancelReview).WithMetadata(new ApiRoleMetadata(ApiRoles.ReviewSpend));

app.MapGet("/api/handover", HandoverConfiguration).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapGet("/api/repos/{repoId}/handover", HandoverConfiguration).WithMetadata(new ApiRoleMetadata(ApiRoles.Read));
app.MapPost("/api/handover/preview", HandoverPreview).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.Handover));
app.MapPost("/api/repos/{repoId}/handover/preview", HandoverPreview).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.Handover));
app.MapPost("/api/handover", Handover).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.Handover));
app.MapPost("/api/repos/{repoId}/handover", Handover).RequireRateLimiting("spend").WithMetadata(new ApiRoleMetadata(ApiRoles.Handover));
app.MapPost("/api/threads", MutateThread).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/repos/{repoId}/threads", MutateThread).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/findings/state", MutateFindingState).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));
app.MapPost("/api/repos/{repoId}/findings/state", MutateFindingState).WithMetadata(new ApiRoleMetadata(ApiRoles.StateMutate));

app.Run();

static IResult ScopeRules(HttpContext context, RepositoryRegistry registry, RepositoryHierarchyCache hierarchyCache)
{
    var (_, repository) = ResolveRepository(context, registry);
    var store = new RepositoryScopeConfigurationStore(repository.Root);
    return Results.Ok(ScopeRuleResponse(store.Read(), ScopeCandidateFiles(repository.Root, hierarchyCache), store));
}

static IResult PreviewScopeRule(HttpContext context, ScopeRuleMutationRequest request,
    RepositoryRegistry registry, RepositoryHierarchyCache hierarchyCache)
{
    var (_, repository) = ResolveRepository(context, registry);
    var store = new RepositoryScopeConfigurationStore(repository.Root);
    var preview = store.Preview(new RepositoryScopeRule(request.Action, request.Pattern, request.Reason),
        ScopeCandidateFiles(repository.Root, hierarchyCache));
    return Results.Ok(new ScopeRuleView(-1, preview.Rule.Action, preview.Rule.Pattern, preview.Rule.Reason,
        preview.MatchedFiles, preview.WiderPattern));
}

static IResult AddScopeRule(HttpContext context, ScopeRuleMutationRequest request,
    RepositoryRegistry registry, RepositoryHierarchyCache hierarchyCache)
{
    var (_, repository) = ResolveRepository(context, registry);
    var store = new RepositoryScopeConfigurationStore(repository.Root);
    var candidates = ScopeCandidateFiles(repository.Root, hierarchyCache);
    var configuration = store.Add(new RepositoryScopeRule(request.Action, request.Pattern, request.Reason),
        candidates, request.ConfirmExpansion);
    return Results.Created($"{context.Request.Path}/{configuration.Rules.Count - 1}",
        ScopeRuleResponse(configuration, candidates, store));
}

static IResult UpdateScopeRule(HttpContext context, int index, ScopeRuleMutationRequest request,
    RepositoryRegistry registry, RepositoryHierarchyCache hierarchyCache)
{
    var (_, repository) = ResolveRepository(context, registry);
    var store = new RepositoryScopeConfigurationStore(repository.Root);
    var candidates = ScopeCandidateFiles(repository.Root, hierarchyCache);
    var configuration = store.Update(index, new RepositoryScopeRule(request.Action, request.Pattern, request.Reason),
        candidates, request.ConfirmExpansion);
    return Results.Ok(ScopeRuleResponse(configuration, candidates, store));
}

static IResult DeleteScopeRule(HttpContext context, int index,
    RepositoryRegistry registry, RepositoryHierarchyCache hierarchyCache)
{
    var (_, repository) = ResolveRepository(context, registry);
    var store = new RepositoryScopeConfigurationStore(repository.Root);
    var candidates = ScopeCandidateFiles(repository.Root, hierarchyCache);
    return Results.Ok(ScopeRuleResponse(store.Delete(index), candidates, store));
}

static ScopeRulesResponse ScopeRuleResponse(
    RepositoryScopeConfiguration configuration,
    IReadOnlyList<string> candidates,
    RepositoryScopeConfigurationStore store) =>
    new(configuration.Schema, configuration.Rules.Select((rule, index) =>
    {
        var preview = store.Preview(rule, candidates);
        return new ScopeRuleView(index, rule.Action, rule.Pattern, rule.Reason,
            preview.MatchedFiles, preview.WiderPattern);
    }).ToArray());

static IReadOnlyList<string> ScopeCandidateFiles(string repositoryRoot, RepositoryHierarchyCache hierarchyCache) =>
    Flatten(hierarchyCache.Get(repositoryRoot).Roots)
        .Where(node => node.Level == ReviewLevel.File)
        .Select(node => node.Path)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

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
    ILogger<Program> logger, Microsoft.Extensions.Options.IOptions<RepositoryOptions> options,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(path);
    var absolute = repository.ResolveFile(relative);
    var limits = options.Value.ContentLimits.Validate();
    var bytes = await BoundedRepositoryFile.ReadAllBytesAsync(
        repository.Root, absolute, limits.MaxFileBytes, cancellationToken);
    var (encoding, content) = DecodeFileContent(bytes);
    var lineEnding = DetectLineEnding(content);
    var findingStates = await new FindingStateStore(repository.Root).ReadAsync(cancellationToken);
    var coverage = CoverageProjection.ForPath(
        CoverageSnapshot.Load(repository.Root),
        CoverageSensor.GitValue(repository.Root, "rev-parse", "--verify", "HEAD"),
        relative,
        file: true);
    logger.LogInformation(new EventId(1101, "FileLoaded"),
        "Loaded {FilePath} from repository {RepositoryId} ({SizeBytes} bytes, {Encoding}, {LineEnding}) in {ElapsedMilliseconds} ms",
        relative, registration.Id, bytes.LongLength, encoding, lineEnding, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new FileResponse(relative, content, repository.ReadMetaDocuments(relative, findingStates),
        bytes.LongLength, lineEnding, encoding, coverage));
}

static IResult ReviewMetaDiagnostics(
    HttpContext context, RepositoryRegistry registry, ReviewMetaIndex index)
{
    var (registration, repository) = ResolveRepository(context, registry);
    return Results.Ok(new
    {
        schemaVersion = ReviewContentLimits.CurrentSchemaVersion,
        repositoryId = registration.Id,
        diagnostics = index.Diagnostics(repository.Root),
    });
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
    RepositoryRegistry registry, ILogger<Program> logger,
    Microsoft.Extensions.Options.IOptions<RepositoryOptions> options, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var relative = repository.NormalizeRelativePath(request.Path);
    var metaPath = repository.FindMetaDocument(relative, request.Kind);
    var limits = options.Value.ContentLimits.Validate();
    FindingIdentityRecord identity;
    using (var metadata = JsonDocument.Parse(await BoundedRepositoryFile.ReadAllTextAsync(
               repository.Root, metaPath, limits.MaxSidecarBytes, cancellationToken)))
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

static async Task<IResult> MutateThread(HttpContext context, ThreadMutationRequest request,
    RepositoryRegistry registry, ILogger<Program> logger,
    Microsoft.Extensions.Options.IOptions<RepositoryOptions> options, CancellationToken cancellationToken)
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
    var limits = options.Value.ContentLimits.Validate();
    var relative = repository.NormalizeRelativePath(request.Path);
    var metaPath = repository.FindMetaDocument(relative, request.Kind);
    var writeLock = ReviewThreadManager.GetWriteLock(metaPath);
    await writeLock.WaitAsync(cancellationToken);
    try
    {
        var root = JsonNode.Parse(await BoundedRepositoryFile.ReadAllTextAsync(
            repository.Root, metaPath, limits.MaxSidecarBytes, cancellationToken))!.AsObject();
        var threads = root["threads"] as JsonArray ?? [];
        root["threads"] = threads;
        JsonObject thread;
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            if (request.Line is null or < 1 || string.IsNullOrWhiteSpace(request.Body))
                throw new ArgumentException("A new thread requires a line and comment body.");
            if (threads.Count >= limits.MaxThreads)
                throw new ReviewContentLimitException($"Review metadata reached the {limits.MaxThreads}-thread limit.");
            var content = await BoundedRepositoryFile.ReadAllTextAsync(
                repository.Root, repository.ResolveFile(relative), limits.MaxFileBytes, cancellationToken);
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
                    ["path"] = relative,
                    ["fingerprint"] = fingerprint,
                    ["contextHash"] = ReviewThreadManager.ComputeContextHash(content, range),
                    ["lastKnownRange"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = request.Line, ["column"] = 1 },
                        ["end"] = new JsonObject { ["line"] = request.Line, ["column"] = 1 },
                    },
                },
                ["status"] = request.Status ?? "open",
                ["anchorState"] = "anchored",
                ["entries"] = new JsonArray(),
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
                ["createdAt"] = DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["body"] = request.Body.Trim(),
            };
            if (!string.IsNullOrWhiteSpace(request.ReplyTo)) entry["replyTo"] = request.ReplyTo;
            thread["entries"]!.AsArray().Add(entry);
        }
        if (request.Status is not null) thread["status"] = request.Status;
        var temporary = metaPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(serialized) > limits.MaxSidecarBytes)
            throw new ReviewContentLimitException($"Review metadata exceeds the {limits.MaxSidecarBytes}-byte sidecar limit.");
        await File.WriteAllTextAsync(temporary, serialized,
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

static IResult Guidelines(HttpContext context, RepositoryRegistry registry, GuidelineStore store,
    Microsoft.Extensions.Options.IOptions<RepositoryOptions> options)
{
    var (_, repository) = ResolveRepository(context, registry);
    var guidelines = store.List(repository.Root);
    return Results.Ok(new
    {
        guidelines,
        catalogue = GuidelineStore.Catalogue,
        traces = BuildGuidelineTraces(repository.Root, guidelines.Select(value => value.Id),
            options.Value.ContentLimits.Validate()),
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

static IReadOnlyList<GuidelineTraceResponse> BuildGuidelineTraces(
    string repositoryRoot, IEnumerable<string> guidelineIds, ReviewContentLimits limits)
{
    var ids = guidelineIds.ToHashSet(StringComparer.Ordinal);
    var findings = ids.ToDictionary(id => id, _ => new List<GuidelineTraceFindingResponse>(), StringComparer.Ordinal);
    var enumeration = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };
    long aggregateBytes = 0;
    foreach (var path in Directory.EnumerateFiles(repositoryRoot, "*.json", enumeration)
                 .Where(path => path.Contains(".review-meta.", StringComparison.Ordinal))
                 .Take(limits.MaxSidecarCount))
    {
        JsonDocument document;
        try
        {
            aggregateBytes = checked(aggregateBytes +
                BoundedRepositoryFile.Length(repositoryRoot, path, limits.MaxSidecarBytes));
            if (aggregateBytes > limits.MaxSidecarAggregateBytes) continue;
            var content = BoundedRepositoryFile.ReadAllText(repositoryRoot, path, limits.MaxSidecarBytes);
            var contract = ReviewMetaJson.Deserialize(content);
            if (contract.Findings.Count > limits.MaxFindings || contract.Threads.Count > limits.MaxThreads) continue;
            document = JsonDocument.Parse(content);
            EnsureBoundedJsonText(document.RootElement, limits.MaxTextFieldCharacters);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                           ArgumentException or InvalidOperationException or
                                           ReviewContentLimitException or OverflowException)
        {
            continue;
        }
        using (document)
        {
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
    }
    return findings.Select(pair => new GuidelineTraceResponse(pair.Key, pair.Value.Count, pair.Value)).ToArray();
}

static void EnsureBoundedJsonText(JsonElement element, int maximumCharacters)
{
    if (element.ValueKind == JsonValueKind.String && (element.GetString()?.Length ?? 0) > maximumCharacters)
        throw new ReviewContentLimitException($"Review metadata text exceeds {maximumCharacters} characters.");
    if (element.ValueKind == JsonValueKind.Object)
        foreach (var property in element.EnumerateObject()) EnsureBoundedJsonText(property.Value, maximumCharacters);
    if (element.ValueKind == JsonValueKind.Array)
        foreach (var item in element.EnumerateArray()) EnsureBoundedJsonText(item, maximumCharacters);
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
        configured.TryGetValue(sensor.Id, out var repositoryConfiguration);
        var availability = repositoryConfiguration?.Enabled == true
            ? await sensor.ProbeAvailabilityAsync(cancellationToken)
            : new SensorAvailability(false, "Sensor is disabled for this repository.");
        descriptors.Add(new
        {
            sensor.Id,
            sensor.Version,
            Scopes = sensor.SupportedScopes.Select(scope => scope.ToString().ToLowerInvariant()).ToArray(),
            Enabled = repositoryConfiguration?.Enabled == true,
            ProfileId = repositoryConfiguration?.ProfileId,
            Configuration = AnalyzerProfileRegistry.IsCommandBacked(sensor.Id)
                ? null
                : repositoryConfiguration?.Configuration,
            availability.Available,
            availability.UnavailableReason,
            availability.ToolVersions,
        });
    }

    return Results.Ok(new { sensors = descriptors });
}

static async Task<IResult> SensorScan(HttpContext context, string id, string? path,
    RepositoryRegistry repositories, SensorRegistry sensors, AnalyzerProfileRegistry profiles, ILogger<Program> logger,
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
        profiles.Resolve(repositoryConfiguration)), cancellationToken);
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
    AgentStudioTaskOptions options,
    ApiSecurity security,
    Microsoft.Extensions.Options.IOptions<RepositoryOptions> repositoryOptions,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var template = await ResolveCanonicalHandoverAsync(repository, request.FilePath, request.ReviewKind,
        request.FindingFingerprint, security.Identity(context).Id,
        repositoryOptions.Value.ContentLimits.Validate(), cancellationToken);
    var card = AgentStudioTaskClient.BuildCard(template,
        options.Project ?? throw new InvalidOperationException("Agent Studio project is not configured."));
    var expectedConfirmation = HandoverConfirmationHash(registration.Id, request.FindingFingerprint, card);
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedConfirmation),
            Encoding.UTF8.GetBytes(request.ConfirmationHash ?? string.Empty)))
        return Results.Conflict(new { error = "The handover preview is missing or stale. Preview the exact card and confirm it again." });
    var result = await client.CreateTaskAsync(template, cancellationToken);
    logger.LogInformation(new EventId(1300, "FindingHandedOver"),
        "Handed over finding for {FilePath} and {ReviewKind} in repository {RepositoryId}; DryRun={DryRun}, TaskId={TaskId}, ElapsedMilliseconds={ElapsedMilliseconds}",
        template.FilePath, request.ReviewKind, registration.Id, result.DryRun, result.TaskId, stopwatch.ElapsedMilliseconds);
    return Results.Ok(result);
}

static async Task<IResult> HandoverPreview(
    HttpContext context,
    HandoverPreviewRequest request,
    RepositoryRegistry registry,
    AgentStudioTaskOptions options,
    ApiSecurity security,
    Microsoft.Extensions.Options.IOptions<RepositoryOptions> repositoryOptions,
    CancellationToken cancellationToken)
{
    var (registration, repository) = ResolveRepository(context, registry);
    var template = await ResolveCanonicalHandoverAsync(repository, request.FilePath, request.ReviewKind,
        request.FindingFingerprint, security.Identity(context).Id,
        repositoryOptions.Value.ContentLimits.Validate(), cancellationToken);
    var card = AgentStudioTaskClient.BuildCard(template,
        options.Project ?? throw new InvalidOperationException("Agent Studio project is not configured."));
    return Results.Ok(new HandoverPreviewResponse(
        HandoverConfirmationHash(registration.Id, request.FindingFingerprint, card), DateTimeOffset.UtcNow, card));
}

static async Task<FindingTaskTemplate> ResolveCanonicalHandoverAsync(
    RepositoryAccess repository,
    string requestedPath,
    string requestedKind,
    string requestedFingerprint,
    string approver,
    ReviewContentLimits contentLimits,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(requestedKind);
    ArgumentException.ThrowIfNullOrWhiteSpace(requestedFingerprint);
    var filePath = repository.NormalizeRelativePath(requestedPath);
    repository.ResolveFile(filePath);
    if (!Enum.TryParse<ReviewKind>(requestedKind, true, out var kind) ||
        !Enum.IsDefined(kind) || int.TryParse(requestedKind, out _))
        throw new ArgumentException("Unsupported review kind.", nameof(requestedKind));
    if (requestedFingerprint.Length != 71 || !requestedFingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
        !requestedFingerprint[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        throw new ArgumentException("Finding fingerprint must be a lowercase sha256 value.", nameof(requestedFingerprint));

    var metaPath = repository.FindMetaDocument(filePath, requestedKind);
    var document = ReviewMetaJson.Deserialize(await BoundedRepositoryFile.ReadAllTextAsync(
        repository.Root, metaPath, contentLimits.MaxSidecarBytes, cancellationToken));
    if (!StringComparer.Ordinal.Equals(document.Unit.Path, filePath) || document.Kind != kind ||
        document.SubjectInputs.Count == 0 ||
        !document.SubjectInputs.Any(input => StringComparer.Ordinal.Equals(input.Path, filePath)))
        throw new StalenessScanException("Review metadata subject does not match the requested current file and kind.");
    foreach (var input in document.SubjectInputs)
    {
        if (!StringComparer.Ordinal.Equals(input.Selector, "file"))
            throw new StalenessScanException("Only current file review findings can be handed over.");
        var inputPath = repository.ResolveFile(input.Path);
        var currentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(
            inputPath, cancellationToken, repository.Root, contentLimits.MaxFileBytes);
        if (!StringComparer.Ordinal.Equals(currentHash, input.ContentHash))
            throw new StalenessScanException("The review finding is stale because its subject changed.");
    }
    var manifest = ReviewSubjectHasher.ComputeManifestHash(document.Unit.Id, document.SubjectInputs);
    if (!StringComparer.Ordinal.Equals(document.ReviewedHash.Value, manifest))
        throw new StalenessScanException("Review metadata has an invalid subject hash.");
    var finding = document.Findings.SingleOrDefault(candidate =>
        StringComparer.Ordinal.Equals(candidate.Fingerprint, requestedFingerprint))
        ?? throw new KeyNotFoundException($"Finding '{requestedFingerprint}' was not found in canonical review metadata.");
    var relativeMetaPath = Path.GetRelativePath(repository.Root, metaPath).Replace('\\', '/');
    return new FindingTaskTemplate(
        finding.Title,
        finding.Locations.FirstOrDefault()?.Path ?? filePath,
        $"{finding.Description}\n\nRecommendation: {finding.Recommendation}",
        kind.ToString().ToLowerInvariant(),
        $"{relativeMetaPath}#{finding.Id}",
        finding.Fingerprint,
        CoverageSensor.GitValue(repository.Root, "rev-parse", "--verify", "HEAD") ?? "unborn",
        approver);
}

static string HandoverConfirmationHash(string repositoryId, string fingerprint, AgentStudioTaskCard card) =>
    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "quality-studio/handover-confirmation/v1",
        repositoryId,
        fingerprint,
        card,
    })));

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

static string RequiredApiRole(HttpContext context) =>
    context.GetEndpoint()?.Metadata.GetMetadata<ApiRoleMetadata>()?.Role ?? "unmapped";

static void AuditAuthorization(
    HttpContext context,
    string? keyId,
    string action,
    string? repositoryId,
    string decision,
    string reason,
    int? result)
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("QualityStudio.Api.SecurityAudit");
    logger.LogInformation(new EventId(1600, "ApiAuthorization"),
        "API authorization key {CredentialKeyId} action {AuthorizationAction} repository {RepositoryId} decision {AuthorizationDecision} reason {AuthorizationReason} result {AuthorizationResult}",
        keyId ?? "unknown", action, repositoryId ?? "none", decision, reason,
        result?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "pending");
}

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
