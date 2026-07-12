using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
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
builder.Services.AddSingleton<RepositoryAccess>();
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

app.MapGet("/api/tree", (string? path, RepositoryAccess repository, ILogger<Program> logger) =>
{
    var stopwatch = Stopwatch.StartNew();
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
        "Loaded {NodeCount} tree roots for {RepositoryPath} in {ElapsedMilliseconds} ms",
        selected.Count, requested, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new TreeResponse(requested, selected.Select(TreeNodeResponse.From).ToArray()));
});

app.MapGet("/api/file", async (string? path, RepositoryAccess repository, CancellationToken cancellationToken) =>
{
    var relative = repository.NormalizeRelativePath(path);
    var absolute = repository.ResolveFile(relative);
    var content = await File.ReadAllTextAsync(absolute, cancellationToken);
    return Results.Ok(new FileResponse(relative, content, repository.ReadMetaDocuments(relative)));
});

app.MapGet("/api/inputs", (RepositoryAccess repository, InputResolver resolver,
    Microsoft.Extensions.Options.IOptions<RepositoryOptions> configured, ILogger<Program> logger) =>
{
    var stopwatch = Stopwatch.StartNew();
    var options = configured.Value;
    var globalDirectory = string.IsNullOrWhiteSpace(options.GlobalInputsDirectory)
        ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
        : options.GlobalInputsDirectory;
    var kinds = Enum.GetValues<ReviewKind>().ToDictionary(
        kind => kind.ToString().ToLowerInvariant(),
        kind => resolver.Resolve(repository.Root, kind.ToString(), ReviewLevel.File,
            globalDirectory, options.InputBudgetCharacters),
        StringComparer.Ordinal);
    logger.LogInformation(new EventId(1102, "InputsResolved"),
        "Resolved review inputs for {KindCount} kinds in {ElapsedMilliseconds} ms",
        kinds.Count, stopwatch.ElapsedMilliseconds);
    return Results.Ok(new { level = "file", kinds });
});

app.MapGet("/api/scan", async (RepositoryAccess repository, StalenessEvaluator evaluator,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    var report = await evaluator.ScanAsync(repository.Root, cancellationToken: cancellationToken);
    logger.LogInformation(new EventId(1200, "ScanCompleted"),
        "Scanned repository with {FileCount} files in {ElapsedMilliseconds} ms",
        report.Files.Count, stopwatch.ElapsedMilliseconds);
    return Results.Ok(report);
});

app.MapGet("/api/security/scan", async (RepositoryAccess repository, GitleaksSecurityScanner scanner,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    var result = await scanner.ScanAsync(new SecurityScanRequest(repository.Root), cancellationToken);
    logger.LogInformation(new EventId(1201, "SecurityScanCompleted"),
        "Scanned repository for secrets with verdict {Verdict} in {ElapsedMilliseconds} ms",
        result.Report.Verdict.ToString().ToLowerInvariant(), stopwatch.ElapsedMilliseconds);
    return Results.Ok(Map(result));
});

app.MapPost("/api/review", () => Results.Problem(
    statusCode: StatusCodes.Status501NotImplemented,
    title: "Review runner unavailable",
    detail: "Review triggering requires the optional QS-6 review runner, which is not available in this build."));

app.MapGet("/api/handover", (AgentStudioTaskOptions options) => Results.Ok(
    new HandoverConfigurationResponse(options.IsTargetConfigured, options.DryRun, options.Project)));

app.MapPost("/api/handover", async (
    HandoverRequest request,
    RepositoryAccess repository,
    AgentStudioTaskClient client,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    var filePath = repository.NormalizeRelativePath(request.FilePath);
    repository.ResolveFile(filePath);
    var result = await client.CreateTaskAsync(new FindingTaskTemplate(
        request.FindingSummary,
        filePath,
        request.FindingText,
        request.ReviewKind,
        request.MetaReference), cancellationToken);
    logger.LogInformation(new EventId(1300, "FindingHandedOver"),
        "Handed over finding for {FilePath} and {ReviewKind}; DryRun={DryRun}, TaskId={TaskId}, ElapsedMilliseconds={ElapsedMilliseconds}",
        filePath, request.ReviewKind, result.DryRun, result.TaskId, stopwatch.ElapsedMilliseconds);
    return Results.Ok(result);
});

app.Run();

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
