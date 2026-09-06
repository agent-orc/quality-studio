using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Behaviour tests for the routes the smoke and run-store suites left untested: risk,
/// guideline CRUD and impact, the default model recommendation, the scope rule listing,
/// run pins, pause, handover configuration and thread mutation. One host serves the
/// whole class - each test works on ids of its own so they do not collide.
/// </summary>
public sealed class ApiRouteCoverageTests(ApiRouteCoverageTests.Fixture fixture)
    : IClassFixture<ApiRouteCoverageTests.Fixture>
{
    [Fact]
    public async Task Risk_returns_rows_and_the_grade_coverage_matrix()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync("/api/risk?days=30", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(30, json.GetProperty("days").GetInt32());
        Assert.Contains(json.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("path").GetString() == "Sample.cs");
        Assert.All(json.GetProperty("matrix").EnumerateArray(),
            cell => Assert.True(cell.GetProperty("files").GetInt32() > 0));
    }

    [Fact]
    public async Task Risk_rejects_a_churn_window_outside_its_range()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync("/api/risk?days=0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Guidelines_lists_installed_rules_next_to_the_catalogue()
    {
        using var client = fixture.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            "/api/guidelines", TestContext.Current.CancellationToken);

        Assert.Contains(json.GetProperty("guidelines").EnumerateArray(),
            guideline => guideline.GetProperty("id").GetString() == "sample-rules");
        Assert.Contains(json.GetProperty("catalogue").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "dotnet-api-safety");
        Assert.True(json.TryGetProperty("traces", out _));
    }

    [Fact]
    public async Task Guideline_can_be_updated_and_deleted()
    {
        using var client = fixture.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/guidelines", new
        {
            id = "lifecycle-rule",
            enabled = true,
            priority = 40,
            kinds = new[] { "code" },
            levels = new[] { "file" },
            content = "Initial wording.",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var updated = await client.PutAsJsonAsync("/api/guidelines/lifecycle-rule", new
        {
            id = "lifecycle-rule",
            enabled = false,
            priority = 55,
            kinds = new[] { "code", "security" },
            levels = new[] { "file" },
            content = "Revised wording.",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var guideline = await updated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(guideline.GetProperty("enabled").GetBoolean());
        Assert.Equal(55, guideline.GetProperty("priority").GetInt32());
        Assert.Contains("Revised wording.", await File.ReadAllTextAsync(
            Path.Combine(fixture.RepositoryRoot, ".quality", "inputs", "lifecycle-rule.md"),
            TestContext.Current.CancellationToken));

        using var deleted = await client.DeleteAsync(
            "/api/guidelines/lifecycle-rule", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(Path.Combine(fixture.RepositoryRoot, ".quality", "inputs", "lifecycle-rule.md")));
    }

    [Fact]
    public async Task Deleting_an_unknown_guideline_reports_not_found()
    {
        using var client = fixture.CreateClient();

        using var response = await client.DeleteAsync(
            "/api/guidelines/never-created", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Catalogue_guideline_is_installed_as_a_repository_file()
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/guidelines/catalog/security-boundaries/install", new { },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var installed = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("security-boundaries", installed.GetProperty("id").GetString());
        Assert.True(File.Exists(Path.Combine(
            fixture.RepositoryRoot, ".quality", "inputs", "security-boundaries.md")));

        // Leave the fixture as it was for the other tests in the class.
        using var cleanup = await client.DeleteAsync(
            "/api/guidelines/security-boundaries", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, cleanup.StatusCode);
    }

    [Fact]
    public async Task Installing_an_unknown_catalogue_guideline_reports_not_found()
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/guidelines/catalog/not-in-the-catalogue/install", new { },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Guideline_impact_preview_reports_the_finding_diff_without_writing_reviews()
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/guidelines/impact", new
        {
            guideline = new
            {
                id = "impact-draft",
                enabled = true,
                priority = 10,
                kinds = new[] { "code" },
                levels = new[] { "file" },
                content = "Flag marker.",
            },
            samplePaths = new[] { "Sample.cs" },
            kind = "code",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(json.GetProperty("changed").GetBoolean());
        Assert.Equal(1, json.GetProperty("addedCount").GetInt32());
        Assert.Equal("impact-draft", Assert.Single(Assert.Single(json.GetProperty("files").EnumerateArray())
            .GetProperty("added").EnumerateArray()).GetProperty("ruleId").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.RepositoryRoot, ".quality", "reviews", "impact")));
    }

    [Fact]
    public async Task Default_model_recommendation_names_a_model_for_a_kind_and_level()
    {
        using var client = fixture.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            "/api/models/default?kind=code&level=file&files=3", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("recommendedModel").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("recommendedThinkingLevel").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("policyVersion").GetString()));
    }

    [Theory]
    [InlineData("/api/models/default?level=file")]
    [InlineData("/api/models/default?kind=code")]
    [InlineData("/api/models/default?kind=code&level=galaxy")]
    public async Task Default_model_recommendation_rejects_an_incomplete_request(string url)
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scope_rules_are_listed_with_their_schema()
    {
        using var client = fixture.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            "/api/scope/rules", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("schema").GetString()));
        Assert.Equal(JsonValueKind.Array, json.GetProperty("rules").ValueKind);
    }

    [Fact]
    public async Task Review_run_pins_are_listed_and_follow_an_unpin()
    {
        using var client = fixture.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>(
            "/api/review/runs/pins", TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, listed.GetProperty("pinnedRunIds").ValueKind);

        // Unpinning a run that was never pinned is a no-op that still answers the listing.
        using var unpinned = await client.DeleteAsync(
            "/api/review/runs/never-pinned/pin", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, unpinned.StatusCode);
        var afterUnpin = await unpinned.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(afterUnpin.GetProperty("pinnedRunIds").EnumerateArray(),
            id => id.GetString() == "never-pinned");
    }

    [Fact]
    public async Task Handover_configuration_reports_the_configured_target()
    {
        using var client = fixture.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>(
            "/api/handover", TestContext.Current.CancellationToken);

        Assert.True(json.GetProperty("targetConfigured").GetBoolean());
        Assert.Equal("QS", json.GetProperty("project").GetString());
    }

    [Fact]
    public async Task Thread_is_opened_on_a_line_replied_to_and_resolved()
    {
        using var client = fixture.CreateClient();

        using var opened = await client.PostAsJsonAsync("/api/threads", new
        {
            path = "Threaded.cs",
            kind = "code",
            body = "Is this branch reachable?",
            humanName = "Ada",
            line = 1,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var thread = await opened.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var threadId = thread.GetProperty("id").GetString()!;
        Assert.Equal("open", thread.GetProperty("status").GetString());
        Assert.Equal("Is this branch reachable?",
            Assert.Single(thread.GetProperty("entries").EnumerateArray()).GetProperty("body").GetString());

        using var resolved = await client.PostAsJsonAsync("/api/threads", new
        {
            path = "Threaded.cs",
            kind = "code",
            threadId,
            body = "Yes - covered by the fixture.",
            humanName = "Grace",
            status = "resolved",
        }, TestContext.Current.CancellationToken);

        if (resolved.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail($"The reply was rejected with {resolved.StatusCode}: " +
                await resolved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var closed = await resolved.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("resolved", closed.GetProperty("status").GetString());
        Assert.Equal(2, closed.GetProperty("entries").GetArrayLength());
    }

    [Theory]
    [InlineData("A comment on nothing", null)]
    [InlineData(null, "archived")]
    public async Task Thread_mutation_rejects_a_request_that_says_nothing_valid(string? body, string? status)
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/threads", new
        {
            path = "Rejected.cs",
            kind = "code",
            body,
            status,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Running_review_is_paused_through_the_api()
    {
        using var client = fixture.CreateClient();
        using var started = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "test-agent",
            force = true,
        }, TestContext.Current.CancellationToken);
        started.EnsureSuccessStatusCode();
        var runId = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetString()!;
        await fixture.Executor.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        using var paused = await client.PostAsJsonAsync(
            $"/api/review/runs/{runId}/pause", new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pausedRun = await paused.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("paused", pausedRun.GetProperty("state").GetString());
        Assert.Equal("paused", await WaitForOneOfAsync(
            client, runId, ["paused"], TestContext.Current.CancellationToken));

        // Leave nothing behind that would hold the queue for the rest of the class.
        fixture.Executor.Release();
        using var cancelled = await client.DeleteAsync(
            $"/api/review/runs/{runId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
    }

    [Fact]
    public async Task Pausing_an_unknown_run_reports_not_found()
    {
        using var client = fixture.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/review/runs/does-not-exist/pause", new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Every read route exists twice - once unscoped and once under /api/repos/{repoId}.
    /// The pair shares one delegate, so this proves the scoped registration resolves and
    /// answers for the default repository instead of duplicating each assertion above.
    /// </summary>
    [Theory]
    [InlineData("tree?path=")]
    [InlineData("risk?days=30")]
    [InlineData("project")]
    [InlineData("file?path=Sample.cs")]
    [InlineData("inputs?path=Sample.cs&level=file")]
    [InlineData("guidelines")]
    [InlineData("rules")]
    [InlineData("scan")]
    [InlineData("sensors")]
    [InlineData("usage")]
    [InlineData("report")]
    [InlineData("scope/rules")]
    [InlineData("review/runs")]
    [InlineData("review/runs/pins")]
    [InlineData("review/runs/retention")]
    [InlineData("handover")]
    public async Task Repository_scoped_read_route_answers_like_its_unscoped_twin(string suffix)
    {
        using var client = fixture.CreateClient();

        using var unscoped = await client.GetAsync($"/api/{suffix}", TestContext.Current.CancellationToken);
        using var scoped = await client.GetAsync(
            $"/api/repos/default/{suffix}", TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, scoped.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, scoped.StatusCode);
        Assert.Equal(unscoped.StatusCode, scoped.StatusCode);
    }

    /// <summary>
    /// Every API route exists twice: once unscoped, once under /api/repos/{repoId}. The pair
    /// shares one delegate, so the risk is not that the handler misbehaves but that a new
    /// route is registered on only one of the two paths - which the scoped clients would
    /// then hit as a 404. This reads the registered endpoints and holds the pairing for all
    /// of them at once; the routes that deliberately exist only once are named here.
    /// </summary>
    [Fact]
    public void Every_api_route_is_registered_both_unscoped_and_repository_scoped()
    {
        string[] deliberatelyUnpaired =
        [
            "/api/repos",                          // the registry collection itself
            "/api/repos/{repoId}",                 // the registration resource itself
            "/api/repos/import-from-agent-studio", // registry-wide import
            "/api/quotas",                         // host-wide, not per repository
            "/api/models",                         // the governed catalog snapshot
            "/api/models/default",                 // a catalog recommendation, not repository data
        ];

        var endpoints = fixture.Endpoints();
        var unscoped = endpoints
            .Where(endpoint => endpoint.Pattern.StartsWith("/api/", StringComparison.Ordinal))
            .Where(endpoint => !endpoint.Pattern.StartsWith("/api/repos/{repoId}/", StringComparison.Ordinal))
            .Where(endpoint => !deliberatelyUnpaired.Contains(endpoint.Pattern, StringComparer.Ordinal))
            .ToArray();

        Assert.NotEmpty(unscoped);
        var missing = unscoped
            .Where(endpoint => !endpoints.Contains(
                endpoint with { Pattern = "/api/repos/{repoId}" + endpoint.Pattern["/api".Length..] }))
            .Select(endpoint => $"{endpoint.Method} {endpoint.Pattern}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_repository_scoped_route_has_an_unscoped_twin()
    {
        var endpoints = fixture.Endpoints();
        var missing = endpoints
            .Where(endpoint => endpoint.Pattern.StartsWith("/api/repos/{repoId}/", StringComparison.Ordinal))
            .Where(endpoint => !endpoints.Contains(
                endpoint with { Pattern = "/api" + endpoint.Pattern["/api/repos/{repoId}".Length..] }))
            .Select(endpoint => $"{endpoint.Method} {endpoint.Pattern}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    private static async Task<string> WaitForOneOfAsync(
        HttpClient client, string runId, string[] expected, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var state = string.Empty;
        do
        {
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/review/runs/{runId}", cancellationToken);
            state = run.GetProperty("state").GetString() ?? string.Empty;
            if (expected.Contains(state, StringComparer.Ordinal)) return state;
            await Task.Delay(50, cancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        return state;
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private TestApplication? application;

        public string RepositoryRoot { get; } = Path.Combine(
            Path.GetTempPath(), "quality-studio-route-tests", Guid.NewGuid().ToString("N"));

        public string HostRoot { get; } = Path.Combine(
            Path.GetTempPath(), "quality-studio-route-hosts", Guid.NewGuid().ToString("N"));

        public GatedExecutorFactory Executor { get; } = new();

        public HttpClient CreateClient() => application!.CreateClient();

        /// <summary>The routes the host registered, as (method, pattern) pairs.</summary>
        public IReadOnlyCollection<RegisteredRoute> Endpoints()
        {
            using var probe = application!.CreateClient(); // forces the host to build
            return application.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                    .Select(method => new RegisteredRoute(method, endpoint.RoutePattern.RawText ?? string.Empty)))
                .ToHashSet();
        }

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(RepositoryRoot);
            Directory.CreateDirectory(HostRoot);
            await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".quality", "inputs"));
            await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, ".quality", "inputs", "sample.md"),
                "---\nid: sample-rules\nenabled: true\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nPrefer explicit names.\n");
            await WriteReviewedFileAsync("Sample.cs");
            // Files of their own for the thread tests: the review run another test starts
            // rewrites the sidecars of Sample.cs, and the index only picks up sidecars
            // written after the host started through an asynchronous watcher event.
            await WriteReviewedFileAsync("Threaded.cs");
            await WriteReviewedFileAsync("Rejected.cs");
            await RunGitAsync("init", "--quiet");
            await RunGitAsync("config", "user.email", "fixture@example.test");
            await RunGitAsync("config", "user.name", "Fixture");
            await RunGitAsync("add", ".");
            await RunGitAsync("commit", "--quiet", "-m", "seed");
            application = new TestApplication(RepositoryRoot, HostRoot, Executor);
        }

        public async ValueTask DisposeAsync()
        {
            Executor.Release();
            if (application is not null) await application.DisposeAsync();
            TemporaryDirectory.Delete(RepositoryRoot);
            TemporaryDirectory.Delete(HostRoot);
        }

        /// <summary>Writes a C# file and the reviewed-code sidecar that routes look up by path.</summary>
        public async Task WriteReviewedFileAsync(string relativePath)
        {
            var name = Path.GetFileNameWithoutExtension(relativePath);
            await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, relativePath),
                $"namespace Sample; public static class {name} {{ public static string Hello() => \"marker\"; }}");
            var directory = Path.Combine(RepositoryRoot, ".quality", "reviews", "files");
            Directory.CreateDirectory(directory);
            var grade = new ReviewGrade(80, GradeBand.B, "Fixture grade.");
            var metadata = new ReviewMetaDocument
            {
                Unit = new ReviewUnit(
                    "qs-v1/generic/file/" + Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(relativePath))),
                    ReviewAdapter.Generic, ReviewLevel.File, relativePath, relativePath),
                ReviewedAt = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero),
                Kind = ReviewKind.Code,
                Reviewer = new ReviewerIdentity("test", "test"),
                ReviewedHash = ManifestHash.Subject(new string('b', 64)),
                SubjectInputs = [new SubjectInputHash(relativePath, "file", "sha256:" + new string('c', 64))],
                ReviewInputs = new ReviewInputs(
                    ManifestHash.ReviewInput(new string('e', 64)), true, [], [],
                    new PromptReference("file-code-review", "1.0.0", "sha256:" + new string('f', 64))),
                Grade = grade,
                Summary = "Fixture review.",
                Aspects = [new ReviewAspect("correctness", "Correctness", grade)],
                Findings = [],
            };
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"{name.ToLowerInvariant()}.review-meta.code.json"),
                ReviewMetaJson.Serialize(metadata));
        }

        private async Task RunGitAsync(params string[] arguments)
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("git")
                {
                    WorkingDirectory = RepositoryRoot,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
    }

    public sealed record RegisteredRoute(string Method, string Pattern);

    /// <summary>An executor the test can hold inside a file review and then let go.</summary>
    public sealed class GatedExecutorFactory : IReviewExecutorFactory
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => gate.TrySetResult();

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new GatedExecutor(this);

        private sealed class GatedExecutor(GatedExecutorFactory owner) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request, bool force, CancellationToken cancellationToken)
            {
                owner.Entered.TrySetResult();
                await owner.gate.Task.WaitAsync(cancellationToken);
                return new ReviewExecutionResult(false, null, null);
            }
        }
    }

    private sealed class FixtureImpactAgent : IReviewAgent
    {
        public string AgentName => "fixture";

        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(
            string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var findings = prompt.Contains("Flag marker.", StringComparison.Ordinal)
                ? "[{\"id\":\"fixture-finding\",\"aspect\":\"correctness\",\"severity\":\"medium\"," +
                  "\"ruleId\":\"impact-draft\",\"title\":\"Marker is flagged\"," +
                  "\"description\":\"The draft policy flags the fixture.\",\"recommendation\":\"Remove the marker.\"," +
                  "\"locations\":[{\"path\":\"Sample.cs\",\"range\":{\"start\":{\"line\":1,\"column\":1}," +
                  "\"end\":{\"line\":1,\"column\":6}}}]}]"
                : "[]";
            var response = "{\"grade\":{\"score\":90,\"band\":\"A\",\"rationale\":\"Fixture.\"}," +
                           "\"summary\":\"Fixture.\",\"aspects\":[{\"id\":\"correctness\",\"title\":\"Correctness\"," +
                           "\"grade\":{\"score\":90,\"band\":\"A\",\"rationale\":\"Fixture.\"}}],\"findings\":" +
                           findings + "}";
            return Task.FromResult(new ReviewAgentResult(Guid.NewGuid().ToString("N"), response));
        }
    }

    private sealed class TestApplication(string root, string contentRoot, GatedExecutorFactory executor)
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
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<IReviewExecutorFactory>();
                services.AddSingleton<IReviewExecutorFactory>(executor);
                services.AddSingleton<IReviewAgent, FixtureImpactAgent>();
                // No deterministic sensor runs here: every route under test is about the
                // API surface, and the real registry would shell out per review attempt.
                services.RemoveAll<IReviewSensor>();
            });
        }
    }
}
