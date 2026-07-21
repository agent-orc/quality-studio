using System.Net;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AgentStudioTaskClientTests
{
    private static readonly FindingTaskTemplate Finding = new(
        "Cache hierarchy construction",
        "src/QualityStudio.Api/Program.cs",
        "The hierarchy is rebuilt for every request. Cache it and invalidate on repository changes.",
        "performance",
        "src/QualityStudio.Api/.quality/reviews/program.review-meta.performance.json#hierarchy-cache");

    [Fact]
    public async Task CreateTask_posts_current_agent_studio_contract_with_client_identity()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"qs-cache-hierarchy\"}", Encoding.UTF8, "application/json"),
        });
        var options = Configured(dryRun: false);
        var client = new AgentStudioTaskClient(new HttpClient(handler), options);

        var result = await client.CreateTaskAsync(Finding, TestContext.Current.CancellationToken);

        Assert.False(result.DryRun);
        Assert.Equal("qs-cache-hierarchy", result.TaskId);
        Assert.Equal(new Uri("http://agent-studio.test/api/tasks"), handler.Request!.RequestUri);
        Assert.Equal("quality-studio", Assert.Single(handler.Request.Headers.GetValues("X-Client-Id")));
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Fix: Cache hierarchy construction in src/QualityStudio.Api/Program.cs", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("QS", body.RootElement.GetProperty("project").GetString());
        Assert.Contains("review re-run comes back fresh+clean", body.RootElement.GetProperty("promptMarkdown").GetString(), StringComparison.Ordinal);
        Assert.Equal("bug", body.RootElement.GetProperty("taskType").GetString());
    }

    [Fact]
    public async Task CreateTask_dry_run_is_default_and_does_not_post()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var output = new StringWriter();
        var client = new AgentStudioTaskClient(new HttpClient(handler), Configured(), output);

        var result = await client.CreateTaskAsync(Finding, TestContext.Current.CancellationToken);

        Assert.True(result.DryRun);
        Assert.Null(result.TaskId);
        Assert.Null(handler.Request);
        Assert.Contains("[AgentStudioTaskClient dry-run] POST /api/tasks", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"project\": \"QS\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProjects_lists_projects_with_repository_paths()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "[{\"id\":\"PROJ-002\",\"displayName\":\"Agent Studio\",\"shortCode\":\"AS\",\"repositoryPath\":\"C:\\\\Projects\\\\agent-taskboard-dev\",\"archived\":false}]",
                Encoding.UTF8, "application/json"),
        });
        var client = new AgentStudioTaskClient(new HttpClient(handler), Configured());

        var projects = await client.GetProjectsAsync(TestContext.Current.CancellationToken);

        var project = Assert.Single(projects);
        Assert.Equal("PROJ-002", project.Id);
        Assert.Equal("Agent Studio", project.DisplayName);
        Assert.Equal("AS", project.ShortCode);
        Assert.Equal(@"C:\Projects\agent-taskboard-dev", project.RepositoryPath);
        Assert.False(project.Archived);
        Assert.Equal(new Uri("http://agent-studio.test/api/projects"), handler.Request!.RequestUri);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
    }

    [Fact]
    public async Task GetProjects_requires_a_configured_base_url()
    {
        var client = new AgentStudioTaskClient(
            new HttpClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            new AgentStudioTaskOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetProjectsAsync(TestContext.Current.CancellationToken));
    }

    private static AgentStudioTaskOptions Configured(bool? dryRun = null) => new()
    {
        BaseUrl = "http://agent-studio.test",
        ClientId = "quality-studio",
        Project = "QS",
        DryRun = dryRun ?? new AgentStudioTaskOptions().DryRun,
    };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
