using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QualityStudio.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.Configure<RepositoryOptions>(builder.Configuration.GetSection(RepositoryOptions.SectionName));
builder.Services.AddSingleton<RepositoryRegistry>();
builder.Services.AddSingleton<StalenessEvaluator>();
builder.Services.AddSingleton<InputResolver>();
builder.Services.AddSingleton<GitleaksBinaryResolver>();
builder.Services.AddSingleton<GitleaksSecurityScanner>();
builder.Services.Configure<AgentStudioTaskOptions>(
    builder.Configuration.GetSection(AgentStudioTaskOptions.SectionName));
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentStudioTaskOptions>>().Value);
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<AgentStudioTaskClient>();
builder.Services.Configure<ReviewJobsOptions>(builder.Configuration.GetSection(ReviewJobsOptions.SectionName));
builder.Services.AddSingleton<ReviewJobService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ReviewJobService>());
var corsOptions = builder.Configuration.GetSection(RepositoryOptions.SectionName).Get<RepositoryOptions>()
    ?? new RepositoryOptions();
builder.Services.AddCors(options => options.AddPolicy("dev-frontend", policy =>
    policy.WithOrigins(corsOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = exception switch
    {
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid repository path"),
        RepositoryRegistryValidationException => (StatusCodes.Status400BadRequest, "Invalid repository configuration"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Repository not found"),
        FileNotFoundException => (StatusCodes.Status404NotFound, "File not found"),
        DirectoryNotFoundException => (StatusCodes.Status503ServiceUnavailable, "Repository unavailable"),
        StalenessScanException => (StatusCodes.Status422UnprocessableEntity, "Repository scan failed"),
        InputFormatException => (StatusCodes.Status422UnprocessableEntity, "Review input is invalid"),
        SecurityScannerUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Security scanner unavailable"),
        HttpRequestException => (StatusCodes.Status502BadGateway, "Agent Studio request failed"),
        InvalidOperationException => (StatusCodes.Status503ServiceUnavailable, "Agent Studio target unavailable"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected API error"),
    };
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("QualityStudio.Api.Errors");
    logger.LogError(new EventId(1000, "ApiRequestFailed"), exception, "API request failed with status {StatusCode}", status);
    await Results.Problem(statusCode: status, title: title, detail: exception?.Message).ExecuteAsync(context);
}));
app.UseStatusCodePages();
app.UseCors("dev-frontend");

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "QualityStudio.Api" }));

app.MapGet("/api/repos", (bool? includeArchived, RepositoryRegistry registry) =>
    Results.Ok(new { repositories = registry.List(includeArchived == true), defaultRepositoryId = RepositoryRegistry.DefaultRepositoryId }));

app.MapPost("/api/repos", async (RepositoryRegistrationRequest request, RepositoryRegistry registry, CancellationToken cancellationToken) =>
{
    var created = await registry.CreateAsync(request, cancellationToken);
    return Results.Created($"/api/repos/{created.Id}", created);
});

app.MapPut("/api/repos/{repoId}", async (string repoId, RepositoryRegistrationRequest request,
    RepositoryRegistry registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.UpdateAsync(repoId, request, cancellationToken)));

app.MapDelete("/api/repos/{repoId}", async (string repoId, RepositoryRegistry registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.ArchiveAsync(repoId, cancellationToken)));

app.MapGet("/api/tree", Tree);
app.MapGet("/api/repos/{repoId}/tree", Tree);
app.MapGet("/api/file", FileContent);
app.MapGet("/api/repos/{repoId}/file", FileContent);
app.MapGet("/api/inputs", Inputs);
app.MapGet("/api/repos/{repoId}/inputs", Inputs);
app.MapGet("/api/scan", Scan);
app.MapGet("/api/repos/{repoId}/scan", Scan);
app.MapGet("/api/security/scan", SecurityScan);
app.MapGet("/api/repos/{repoId}/security/scan", SecurityScan);

app.MapPost("/api/review", StartReview);
app.MapPost("/api/repos/{repoId}/review", StartReview);
app.MapGet("/api/review/runs", ReviewRuns);
app.MapGet("/api/repos/{repoId}/review/runs", ReviewRuns);
app.MapGet("/api/review/runs/{id}", ReviewRun);
app.MapGet("/api/repos/{repoId}/review/runs/{id}", ReviewRun);
app.MapDelete("/api/review/runs/{id}", CancelReview);
app.MapDelete("/api/repos/{repoId}/review/runs/{id}", CancelReview);

app.MapGet("/api/handover", HandoverConfiguration);
app.MapGet("/api/repos/{repoId}/handover", HandoverConfiguration);
app.MapPost("/api/handover", Handover);
app.MapPost("/api/repos/{repoId}/handover", Handover);

app.Run();

static IResult Tree(HttpContext context, string? path, RepositoryRegistry registry, ILogger<Program> logger)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var requested = repository.NormalizeRelativePath(path);
    var projects = RepositoryHierarchyBuilder.BuildDotNet(repository.Root);
    ReviewMetaDiscovery.AttachDiscovered(repository.Root, projects);
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
    return Results.Ok(new TreeResponse(requested, selected.Select(TreeNodeResponse.From).ToArray()));
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
    logger.LogInformation(new EventId(1101, "FileLoaded"),
        "Loaded {FilePath} from repository {RepositoryId} ({SizeBytes} bytes, {Encoding}, {LineEnding}) in {ElapsedMilliseconds} ms",
        relative, registration.Id, bytes.LongLength, encoding, lineEnding, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new FileResponse(relative, content, repository.ReadMetaDocuments(relative), bytes.LongLength, lineEnding, encoding));
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
        return ("other", Encoding.UTF8.GetString(bytes));
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
    var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : registration.GlobalInputsDirectory;
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

static async Task<IResult> Scan(HttpContext context, RepositoryRegistry registry, StalenessEvaluator evaluator,
    ILogger<Program> logger, CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    var (registration, repository) = ResolveRepository(context, registry);
    var report = await evaluator.ScanAsync(repository.Root, cancellationToken: cancellationToken);
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
    var result = await scanner.ScanAsync(new SecurityScanRequest(repository.Root), cancellationToken);
    logger.LogInformation(new EventId(1201, "SecurityScanCompleted"),
        "Scanned repository {RepositoryId} for secrets with verdict {Verdict} in {ElapsedMilliseconds} ms",
        registration.Id, result.Report.Verdict.ToString().ToLowerInvariant(), stopwatch.ElapsedMilliseconds);
    return Results.Ok(Map(result));
}

static IResult StartReview(HttpContext context, StartReviewRequest request, RepositoryRegistry registry, ReviewJobService jobs)
{
    var repository = registry.Get(RouteRepositoryId(context));
    var run = jobs.Enqueue(repository.Id, request);
    var basePath = RouteRepositoryId(context) is null ? "/api/review/runs" : $"/api/repos/{Uri.EscapeDataString(repository.Id)}/review/runs";
    return Results.Accepted($"{basePath}/{run.Id}", run);
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

static (RepositoryRegistration Registration, RepositoryAccess Access) ResolveRepository(
    HttpContext context, RepositoryRegistry registry)
{
    var id = RouteRepositoryId(context);
    var registration = registry.Get(id);
    return (registration, new RepositoryAccess(registration.RootPath));
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
