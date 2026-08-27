using System.Net.Http.Json;
using System.Text.Json;

namespace QualityStudio.Api;

public sealed class AgentStudioTaskOptions
{
    public const string SectionName = "AgentStudio";

    public string? BaseUrl { get; set; }
    public string? ClientId { get; set; }
    public string? Project { get; set; }
    public bool DryRun { get; set; } = true;

    public bool IsTargetConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(Project);
}

public sealed record FindingTaskTemplate(
    string FindingSummary,
    string FilePath,
    string FindingText,
    string ReviewKind,
    string MetaReference);

public sealed record AgentStudioTaskCard(
    string Title,
    string Project,
    string PromptMarkdown,
    string TaskType = "bug");

public sealed record AgentStudioTaskResult(bool DryRun, string? TaskId, AgentStudioTaskCard Card);

public sealed record AgentStudioProject(
    string Id,
    string DisplayName,
    string? ShortCode,
    string? RepositoryPath,
    bool Archived);

/// <summary>HTTP adapter owned by the API host, outside the analysis package.</summary>
public sealed class AgentStudioTaskClient
{
    public const string ClientIdHeader = "X-Client-Id";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HttpClient httpClient;
    private readonly AgentStudioTaskOptions options;
    private readonly TextWriter dryRunOutput;

    public AgentStudioTaskClient(
        HttpClient httpClient,
        AgentStudioTaskOptions options,
        TextWriter? dryRunOutput = null)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.dryRunOutput = dryRunOutput ?? Console.Out;
    }

    public async Task<AgentStudioTaskResult> CreateTaskAsync(
        FindingTaskTemplate finding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (!options.IsTargetConfigured)
            throw new InvalidOperationException(
                "Agent Studio target requires an absolute BaseUrl, ClientId, and Project.");

        var card = BuildCard(finding, options.Project!);
        if (options.DryRun)
        {
            await dryRunOutput.WriteLineAsync(
                $"[AgentStudioTaskClient dry-run] POST /api/tasks{Environment.NewLine}" +
                JsonSerializer.Serialize(card, JsonOptions));
            return new AgentStudioTaskResult(true, null, card);
        }

        var endpoint = new Uri(new Uri(options.BaseUrl!.TrimEnd('/') + "/", UriKind.Absolute), "api/tasks");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(card, options: JsonOptions),
        };
        request.Headers.Add(ClientIdHeader, options.ClientId);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateTaskResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(created?.Id))
            throw new HttpRequestException("Agent Studio returned a successful response without a task id.");

        return new AgentStudioTaskResult(false, created.Id, card);
    }

    public async Task<IReadOnlyList<AgentStudioProject>> GetProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Agent Studio target requires an absolute BaseUrl.");

        var endpoint = new Uri(new Uri(options.BaseUrl!.TrimEnd('/') + "/", UriKind.Absolute), "api/projects");
        using var response = await httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var projects = await response.Content.ReadFromJsonAsync<List<AgentStudioProject>>(
            JsonOptions, cancellationToken).ConfigureAwait(false);
        return projects ?? [];
    }

    public static AgentStudioTaskCard BuildCard(FindingTaskTemplate finding, string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.FindingSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.FilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.FindingText);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.ReviewKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.MetaReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var prompt = $"""
            Address this Quality Studio review finding.

            File: {finding.FilePath}
            Review kind: {finding.ReviewKind}
            Review meta: {finding.MetaReference}

            Finding:
            {finding.FindingText}

            Acceptance criteria:
            - Re-run the {finding.ReviewKind} review after the fix.
            - The review re-run comes back fresh+clean.
            """;

        return new AgentStudioTaskCard(
            $"Fix: {finding.FindingSummary} in {finding.FilePath}",
            project,
            prompt);
    }

    private sealed record CreateTaskResponse(string Id);
}
