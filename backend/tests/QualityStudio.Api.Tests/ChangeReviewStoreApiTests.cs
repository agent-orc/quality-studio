using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// The change-review store: a verdict is computed at most once per key, can be ingested from Agent
/// Studio, and is read back per range. The reviewer is a fake that counts its invocations.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class ChangeReviewStoreApiTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "qs-change-store-repos", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "qs-change-store-hosts", Guid.NewGuid().ToString("N"));
    private readonly CountingAgentFactory agents = new();
    private TestApplication? application;
    private string baseSha = string.Empty;
    private string headSha = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Second_post_for_the_same_key_returns_the_stored_document_without_a_model_call()
    {
        using var client = application!.CreateClient();

        using var first = await client.PostAsJsonAsync("/api/repos/default/change-review",
            new { baseSha, headSha }, Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("computed", Assert.Single(first.Headers.GetValues(ChangeReviewEndpoints.SourceHeader)));
        Assert.Equal(1, agents.Invocations);
        var firstBody = await first.Content.ReadAsStringAsync(Token);

        using var second = await client.PostAsJsonAsync("/api/repos/default/change-review",
            new { baseSha, headSha }, Token);
        using var unscoped = await client.PostAsJsonAsync("/api/change-review",
            new { baseSha, headSha, model = "another-model" }, Token);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("stored", Assert.Single(second.Headers.GetValues(ChangeReviewEndpoints.SourceHeader)));
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync(Token));
        Assert.Equal(firstBody, await unscoped.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, agents.Invocations);

        var stored = await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token);
        var record = Assert.Single(stored.GetProperty("records").EnumerateArray());
        Assert.Equal("quality-studio", record.GetProperty("provenance").GetProperty("producer").GetString());
        Assert.True(record.GetProperty("current").GetBoolean());
        Assert.Equal("pass", record.GetProperty("document").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task An_infrastructure_verdict_is_stored_but_never_answers_a_later_post()
    {
        agents.Failure = new InvalidOperationException("provider is down");
        using var client = application!.CreateClient();

        var failed = await client.PostAsJsonAsync("/api/repos/default/change-review", new { baseSha, headSha }, Token);
        Assert.Equal("provider-unavailable",
            (await failed.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("verdict").GetString());
        var unavailable = await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token);
        Assert.False(Assert.Single(unavailable.GetProperty("records").EnumerateArray()).GetProperty("decisive").GetBoolean());

        agents.Failure = null;
        var retried = await client.PostAsJsonAsync("/api/repos/default/change-review", new { baseSha, headSha }, Token);

        Assert.Equal("pass", (await retried.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("verdict").GetString());
        Assert.Equal(2, agents.Invocations);
        var replaced = await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token);
        Assert.Equal("pass", Assert.Single(replaced.GetProperty("records").EnumerateArray())
            .GetProperty("document").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Ingesting_the_same_document_twice_stores_one_record_and_answers_post_without_a_model_call()
    {
        using var client = application!.CreateClient();
        var currentKey = (await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token)).GetProperty("currentKey");
        var document = Document(currentKey, "concerns");
        var request = new
        {
            document,
            provenance = new { producer = "agent-studio-review-plane", cardKey = "AGT-3352", round = 2, attemptId = "attempt-7" },
        };

        using var first = await client.PutAsJsonAsync("/api/v1/repos/default/change-reviews", request, Token);
        using var second = await client.PutAsJsonAsync("/api/v1/repos/default/change-reviews", request, Token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("stored", (await first.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("unchanged", (await second.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("outcome").GetString());
        var range = await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token);
        var record = Assert.Single(range.GetProperty("records").EnumerateArray());
        var provenance = record.GetProperty("provenance");
        Assert.Equal("agent-studio-review-plane", provenance.GetProperty("producer").GetString());
        Assert.Equal("AGT-3352", provenance.GetProperty("cardKey").GetString());
        Assert.Equal(2, provenance.GetProperty("round").GetInt32());
        Assert.Equal("attempt-7", provenance.GetProperty("attemptId").GetString());
        Assert.Single(Directory.GetFiles(QualityDataRoot.Combine(repositoryRoot, ChangeReviewStore.DirectoryName, baseSha, headSha)));
        var finding = Assert.Single(range.GetProperty("excerpts").EnumerateObject());
        Assert.Contains(finding.Value.EnumerateArray(), line => line.GetProperty("anchor").GetBoolean() &&
            line.GetProperty("text").GetString() == "await operation.RunAsync();");

        using var post = await client.PostAsJsonAsync("/api/repos/default/change-review", new { baseSha, headSha }, Token);
        Assert.Equal(0, agents.Invocations);
        Assert.Equal("stored", Assert.Single(post.Headers.GetValues(ChangeReviewEndpoints.SourceHeader)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(document.ToJsonString()),
            JsonNode.Parse(await post.Content.ReadAsStringAsync(Token))));

        var conflicting = Document(currentKey, "pass");
        using var conflict = await client.PutAsJsonAsync("/api/v1/repos/default/change-reviews",
            new { document = conflicting, provenance = new { producer = "quality-studio" } }, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Theory]
    [InlineData(2, "https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v1.schema.json")]
    [InlineData(1, "https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v2.schema.json")]
    public async Task A_document_with_an_unknown_schema_version_is_refused_with_422(int version, string schema)
    {
        using var client = application!.CreateClient();
        var key = (await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token)).GetProperty("currentKey");
        var document = Document(key, "pass");
        document["schemaVersion"] = version;
        document["schema"] = schema;

        using var response = await client.PutAsJsonAsync("/api/v1/repos/default/change-reviews",
            new { document, provenance = new { producer = "agent-studio-review-plane" } }, Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("Unsupported change-review document version", problem.GetProperty("title").GetString());
        Assert.False(Directory.Exists(QualityDataRoot.Combine(repositoryRoot, ChangeReviewStore.DirectoryName, baseSha)));
    }

    [Fact]
    public async Task Ingest_refuses_documents_that_do_not_conform_or_name_another_repository()
    {
        using var client = application!.CreateClient();
        var key = (await client.GetFromJsonAsync<JsonElement>(RangeUrl(), Token)).GetProperty("currentKey");
        var foreign = Document(key, "pass");
        foreign["repository"] = "elsewhere";
        var malformed = Document(key, "maybe");
        var unknownProducer = Document(key, "pass");

        foreach (var (document, producer) in new[] { (foreign, "quality-studio"), (malformed, "quality-studio"), (unknownProducer, "someone") })
        {
            using var response = await client.PutAsJsonAsync("/api/v1/repos/default/change-reviews",
                new { document, provenance = new { producer } }, Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
        using var missing = await client.PutAsJsonAsync("/api/v1/repos/unknown/change-reviews",
            new { document = Document(key, "pass"), provenance = new { producer = "quality-studio" } }, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Range_reports_missing_commits_and_refuses_non_sha_ranges()
    {
        using var client = application!.CreateClient();
        var absent = new string('c', 40);

        var range = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/repos/default/change-reviews?base={baseSha}&head={absent}&previous={baseSha}", Token);
        using var branch = await client.GetAsync($"/api/v1/repos/default/change-reviews?base=main&head={headSha}", Token);

        Assert.True(range.GetProperty("commits").GetProperty("base").GetBoolean());
        Assert.False(range.GetProperty("commits").GetProperty("head").GetBoolean());
        Assert.Equal(JsonValueKind.Null, range.GetProperty("currentKey").ValueKind);
        Assert.Empty(range.GetProperty("records").EnumerateArray());
        Assert.False(range.GetProperty("roundOnly").GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, branch.StatusCode);
        Assert.Equal(0, agents.Invocations);
    }

    [Fact]
    public async Task Capabilities_announce_the_change_review_store()
    {
        using var client = application!.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities", Token);

        Assert.Equal("agent-studio-integration/1", capabilities.GetProperty("contract").GetString());
        Assert.Contains("agent-studio-integration/1",
            capabilities.GetProperty("contracts").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("change-review-store",
            capabilities.GetProperty("features").EnumerateArray().Select(value => value.GetString()));
    }

    private string RangeUrl() => $"/api/v1/repos/default/change-reviews?base={baseSha}&head={headSha}";

    private JsonObject Document(JsonElement key, string verdict) => new()
    {
        ["schema"] = ChangeReviewVerdictService.SchemaId,
        ["schemaVersion"] = 1,
        ["repository"] = key.GetProperty("repository").GetString(),
        ["baseSha"] = baseSha,
        ["headSha"] = headSha,
        ["verdict"] = verdict,
        ["policyHash"] = key.GetProperty("policyHash").GetString(),
        ["ruleSetHash"] = key.GetProperty("ruleSetHash").GetString(),
        ["catalogueVersion"] = key.GetProperty("catalogueVersion").GetString(),
        ["findings"] = verdict == "concerns"
            ? new JsonArray(new JsonObject
            {
                ["id"] = "finding-" + new string('f', 64),
                ["fingerprint"] = "sha256:" + new string('f', 64),
                ["ruleId"] = "QS-CS-003",
                ["ruleVersion"] = "1.0.0",
                ["severity"] = "medium",
                ["path"] = "src/Work.cs",
                ["side"] = "head",
                ["line"] = 2,
                ["message"] = "Missing cancellation handling",
                ["evidenceChecked"] = "The added await",
                ["missing"] = "CancellationToken forwarding",
                ["disposition"] = "concerns",
            })
            : new JsonArray(),
        ["model"] = "review-plane-model",
        ["durationMilliseconds"] = 11000,
    };

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(hostRoot);
        await GitTestRepository.InitializeAsync(repositoryRoot, Token);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "src"));
        var source = Path.Combine(repositoryRoot, "src", "Work.cs");
        await File.WriteAllTextAsync(source, "class Work {}\n", Token);
        await GitTestRepository.RunAsync(repositoryRoot, Token, "add", "-A");
        await GitTestRepository.RunAsync(repositoryRoot, Token, "commit", "-qm", "base");
        baseSha = (await GitTestRepository.RunForOutputAsync(repositoryRoot, Token, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, "class Work {}\nawait operation.RunAsync();\n", Token);
        await GitTestRepository.RunAsync(repositoryRoot, Token, "add", "-A");
        await GitTestRepository.RunAsync(repositoryRoot, Token, "commit", "-qm", "head");
        headSha = (await GitTestRepository.RunForOutputAsync(repositoryRoot, Token, "rev-parse", "HEAD")).Trim();
        application = new TestApplication(repositoryRoot, hostRoot, agents);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        TemporaryDirectory.Delete(QualityDataRoot.For(repositoryRoot));
        TemporaryDirectory.Delete(repositoryRoot);
        TemporaryDirectory.Delete(hostRoot);
    }

    private sealed class CountingAgentFactory : IChangeReviewAgentFactory
    {
        private int invocations;
        public int Invocations => Volatile.Read(ref invocations);
        public Exception? Failure { get; set; }

        public IReviewAgent Create(ChangeReviewVerdictRequest request) => new Agent(this);

        private sealed class Agent(CountingAgentFactory owner) : IReviewAgent
        {
            public string AgentName => "counting-fake";
            public string? Model => "fake-model";

            public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.invocations);
                return owner.Failure is { } failure
                    ? Task.FromException<ReviewAgentResult>(failure)
                    : Task.FromResult(new ReviewAgentResult("fake-run", """{"findings":[]}"""));
            }
        }
    }

    private sealed class TestApplication(string root, string contentRoot, IChangeReviewAgentFactory agents)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = Path.GetDirectoryName(root),
                    // Each test spends at most a handful of calls; the fixed window must not be what it measures.
                    ["QualityStudio:Security:SpendRequestsPerMinute"] = "100",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<IChangeReviewAgentFactory>();
                services.AddSingleton(agents);
            });
        }
    }
}
