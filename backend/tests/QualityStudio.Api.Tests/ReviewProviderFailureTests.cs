using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
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
/// Run-level failure handling (QS-109, evaluation defects D2 and D4): identical provider or login
/// failures stop a sweep, the login state is shown with the quota, and refused answers are kept in
/// the run journal.
/// </summary>
public sealed class ReviewProviderFailureTests
{
    private const string Cli = "test-agent";

    [Fact]
    public async Task Identical_auth_failures_stop_the_run_skip_the_rest_and_mark_the_login_failed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await Fixture.CreateAsync(cancellationToken);
        var executor = new ScriptedExecutorFactory((_, _, _) => throw AuthFailure());
        await using var application = fixture.CreateApplication(executor);
        using var client = application.CreateClient();

        var run = await StartAndWaitAsync(client, "failed", cancellationToken);

        Assert.Equal(3, executor.Calls);
        Assert.Equal(3, run.GetProperty("failedFiles").GetInt32());
        Assert.Equal(2, run.GetProperty("skippedFiles").GetInt32());
        Assert.Equal("skipped", run.GetProperty("aggregateState").GetString());
        var stopReason = run.GetProperty("stopReason").GetString()!;
        Assert.Contains("Stopped after 3 identical authentication failures from test-agent", stopReason, StringComparison.Ordinal);
        Assert.Contains("401 Unauthorized", stopReason, StringComparison.Ordinal);
        Assert.All(run.GetProperty("files").EnumerateArray().Where(file => file.GetProperty("state").GetString() == "skipped"),
            file => Assert.Equal(stopReason, file.GetProperty("error").GetString()));
        var report = new QualityRunReportStore(fixture.RepositoryRoot).Load(run.GetProperty("id").GetString()!);
        Assert.Equal("failed", report.Run.State);

        var auth = await AuthStateAsync(client, cancellationToken);
        Assert.Equal("failed", auth.GetProperty("state").GetString());
        Assert.Equal("review-run", auth.GetProperty("source").GetString());
        Assert.Contains("401 Unauthorized", auth.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reached_provider_restarts_the_count_and_a_refused_answer_is_kept_in_the_journal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await Fixture.CreateAsync(cancellationToken);
        const string raw = "Sorry, I will describe the problems in prose instead.";
        var executor = new ScriptedExecutorFactory((call, request, rejected) =>
        {
            if (request.Level != ReviewLevel.File) return new ReviewExecutionResult(false, null, null);
            if (call != 3) throw AuthFailure();
            rejected(ReviewResponseRejection.Capture("quality-answer", 2, false, "The agent returned no JSON object.",
                raw, DateTimeOffset.UtcNow) with { Path = request.FilePath, Kind = request.Kind, Level = "file" });
            throw new ReviewResponseException("The agent returned no JSON object.");
        });
        await using var application = fixture.CreateApplication(executor);
        using var client = application.CreateClient();

        var run = await StartAndWaitAsync(client, "done", cancellationToken);

        // Failures 1-2, then an answered request, then failures 4-5: never three identical in a row.
        Assert.Equal(6, executor.Calls);
        Assert.Equal(5, run.GetProperty("failedFiles").GetInt32());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("stopReason").ValueKind);
        var rejection = Assert.Single(fixture.Store.ReadRejections(run.GetProperty("id").GetString()!));
        Assert.Equal(raw, rejection.Response);
        Assert.Equal(("quality-answer", 2, false), (rejection.AgentRunId, rejection.Attempt, rejection.Retried));
        Assert.EndsWith(".cs", rejection.Path, StringComparison.Ordinal);
        // The aggregate review that closed the run reached the provider, so that is the last word.
        Assert.Equal("ok", (await AuthStateAsync(client, cancellationToken)).GetProperty("state").GetString());
    }

    [Fact]
    public void Classifier_recognises_login_failures_and_ignores_run_specific_tokens()
    {
        var first = ProviderFailureClassifier.Classify(AuthFailure());
        var second = ProviderFailureClassifier.Classify(AuthFailure());
        var timeout = ProviderFailureClassifier.Classify(new ReviewAgentRunException("quality-x",
            new TokenUsage(null, null, null, null, 0), null,
            new ReviewAgentAttachTimeoutException("quality-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(90))));

        Assert.NotNull(first);
        Assert.True(first.IsAuth);
        Assert.Equal(first.Signature, second!.Signature);
        Assert.DoesNotContain("quality-", first.Signature, StringComparison.Ordinal);
        Assert.Equal(ProviderFailureKind.Provider, timeout!.Kind);
        Assert.Null(ProviderFailureClassifier.Classify(new ReviewResponseException("The agent returned invalid JSON.")));
        Assert.Null(ProviderFailureClassifier.Classify(new IOException("The file is locked.")));
        Assert.Null(ProviderFailureClassifier.Classify(new ReviewRunException("The review target changed.")));
    }

    [Fact]
    public void Login_state_combines_review_evidence_with_the_quota_probe()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));
        var tracker = new ProviderAuthStateTracker(clock);
        var probedAt = clock.GetUtcNow();

        Assert.Equal(ProviderAuthState.Unknown, tracker.Describe("claude").State);
        Assert.Equal(ProviderAuthState.Unknown, tracker.Describe("claude", "Usage endpoint timed out.", probedAt).State);
        var probe = tracker.Describe("claude", "HTTP 401: OAuth token has expired", probedAt);
        Assert.Equal((ProviderAuthState.Failed, ProviderAuthState.QuotaProbeSource), (probe.State, probe.Source));

        clock.Advance(TimeSpan.FromMinutes(1));
        tracker.RecordSuccess("claude");
        Assert.Equal(ProviderAuthState.Ok, tracker.Describe("claude", "HTTP 401: OAuth token has expired", probedAt).State);

        tracker.RecordFailure("codex", "401 Unauthorized");
        var codex = tracker.Describe("codex");
        Assert.Equal((ProviderAuthState.Failed, ProviderAuthState.ReviewRunSource), (codex.State, codex.Source));
    }

    [Fact]
    public void Run_delta_reports_a_missed_finding_on_unchanged_code_as_not_reobserved()
    {
        var missed = "sha256:" + new string('a', 64);
        var fixedFinding = "sha256:" + new string('b', 64);
        var prior = BuildReport("review-prior", 0, [missed, fixedFinding], new Dictionary<string, string>
        {
            [missed] = "open",
            [fixedFinding] = "open",
        }, []);

        var current = BuildReport("review-current", 1, [], new Dictionary<string, string>
        {
            [missed] = "not-reobserved",
            [fixedFinding] = "resolved",
        }, [prior]);

        Assert.Equal("available", current.Delta.Status);
        Assert.Equal(new[] { fixedFinding }, current.Delta.Resolved);
        Assert.Equal(new[] { missed }, current.Delta.NotReobserved);
        Assert.Contains("1 resolved · 1 not re-observed", QualityRunReportRenderer.Render(current, QualityReportFormat.Markdown),
            StringComparison.Ordinal);
        var roundTrip = QualityRunReportJson.Deserialize(QualityRunReportJson.Serialize(current));
        Assert.Equal(new[] { missed }, roundTrip.Delta.NotReobserved);
    }

    private static QualityRunReportDocument BuildReport(
        string runId,
        int minutes,
        IReadOnlyList<string> fingerprints,
        IReadOnlyDictionary<string, string> states,
        IReadOnlyList<QualityRunReportDocument> prior)
    {
        var at = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
        var hash = "sha256:" + new string('e', 64);
        var manifest = new ReviewRunManifest(runId, "default", new("unit-file", "App.cs", "src/App.cs"), "file",
            "code", "claude-opus-5-5", "claude", at, [new("unit-file", "App.cs", "src/App.cs", hash)], null);
        var status = new ReviewRunStatus(runId, "done", 1, 1, 0, 1, at, at, at.AddSeconds(5), [], 1,
            new(100, 20, 10, 5, 1500));
        var metadata = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["reviewedHash"] = new JsonObject { ["value"] = hash },
            ["grade"] = new JsonObject { ["score"] = 80, ["band"] = "B", ["rationale"] = "Fixture." },
            ["summary"] = "Fixture.",
            ["findings"] = new JsonArray(fingerprints.Select(fingerprint => (JsonNode)new JsonObject
            {
                ["id"] = "finding-" + fingerprint[7..],
                ["ruleId"] = "built-in:code",
                ["aspect"] = "correctness",
                ["severity"] = "medium",
                ["title"] = "Fixture finding",
                ["description"] = "Fixture.",
                ["recommendation"] = "Fixture.",
                ["fingerprint"] = fingerprint,
                ["locations"] = new JsonArray(new JsonObject { ["path"] = "src/App.cs" }),
            }).ToArray()),
        };
        var snapshot = new ReviewObservationSnapshot("reviews/App.review-meta.code.json", hash, at.AddSeconds(4),
            metadata.ToJsonString(), states);
        return QualityRunReportFactory.Build(manifest, status,
            [new ReviewRunFileTransition("src/App.cs", "done", at, at.AddSeconds(4), runId, null)],
            new Dictionary<string, ReviewObservationSnapshot> { ["src/App.cs"] = snapshot },
            Path.GetTempPath(), "Synthetic fixture", 1, prior);
    }

    private static ReviewAgentRunException AuthFailure()
    {
        var runId = "quality-" + Guid.NewGuid().ToString("N");
        return new ReviewAgentRunException(runId, new TokenUsage(null, null, null, null, 120), null,
            new ReviewAgentRunAbortedException(runId, RunOutcome.Failed,
                "401 Unauthorized: your access token could not be refreshed. Please run codex login."));
    }

    private static async Task<JsonElement> StartAndWaitAsync(HttpClient client, string expected, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = ".",
            kind = "code",
            cliType = Cli,
            model = "claude-sonnet-5",
            thinkingLevel = "high",
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
        JsonElement run;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            if (run.GetProperty("state").GetString() == expected) return run;
            await Task.Delay(50, cancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail($"Run {runId} ended in '{run.GetProperty("state").GetString()}', expected '{expected}'.");
        return run;
    }

    private static async Task<JsonElement> AuthStateAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var quotas = await client.GetFromJsonAsync<JsonElement>("/api/quotas", cancellationToken);
        return Assert.Single(quotas.GetProperty("auth").EnumerateArray(),
            auth => auth.GetProperty("provider").GetString() == Cli);
    }

    private sealed class ScriptedExecutorFactory(
        Func<int, ReviewRequest, Action<ReviewResponseRejection>, ReviewExecutionResult> script) : IReviewExecutorFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        private ReviewExecutionResult Next(ReviewRequest request, Action<ReviewResponseRejection> rejected) =>
            script(Interlocked.Increment(ref calls), request, rejected);

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            throw new InvalidOperationException("The run service must pass the rejection callback.");

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded,
            Action<ReviewResponseRejection> responseRejected) => new Executor(this, responseRejected);

        private sealed class Executor(ScriptedExecutorFactory owner, Action<ReviewResponseRejection> rejected) : IReviewExecutor
        {
            public Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request, bool force, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Next(request, rejected));
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current += by;
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string repositoryRoot, string hostRoot)
        {
            RepositoryRoot = repositoryRoot;
            HostRoot = hostRoot;
            Store = new ReviewRunStore(repositoryRoot);
        }

        public string RepositoryRoot { get; }
        public string HostRoot { get; }
        public ReviewRunStore Store { get; }

        public static async Task<Fixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-provider-failure-tests", id, "repository");
            var hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-provider-failure-tests", id, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            foreach (var name in new[] { "First", "Second", "Third", "Fourth", "Fifth" })
                await File.WriteAllTextAsync(Path.Combine(repositoryRoot, name + ".cs"),
                    $"namespace Sample; public static class {name} {{ }}", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            return new Fixture(repositoryRoot, hostRoot);
        }

        public WebApplicationFactory<Program> CreateApplication(IReviewExecutorFactory executor) =>
            new Application(RepositoryRoot, HostRoot, executor);

        public void Dispose() => TemporaryDirectory.Delete(Path.GetDirectoryName(RepositoryRoot)!);
    }

    private sealed class Application(string repositoryRoot, string contentRoot, IReviewExecutorFactory executor)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = repositoryRoot,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<IReviewExecutorFactory>();
                services.AddSingleton(executor);
                services.RemoveAll<IReviewSensor>();
                // One file at a time, so "consecutive" is the script order.
                services.PostConfigure<ReviewJobsOptions>(options =>
                {
                    options.MaxConcurrency = 1;
                    options.ProviderFailureStopThreshold = 3;
                });
            });
        }
    }
}
