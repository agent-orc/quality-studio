using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSecurityTests
{
    [Fact]
    public async Task Hosted_repository_boundaries_cover_read_export_and_mutation_routes()
    {
        var outbound = new RejectUnexpectedOutboundRequest();
        await using var target = application!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(new HttpClient(outbound))));
        using var alice = CreateClient(target, "alice", AliceToken);
        using var bob = CreateClient(target, "bob", BobToken);
        SeedBoundaryOutcome(ForeignRepositoryRoot, "foreign", "foreign-outcome", "Foreign.cs", "foreign-private-marker");
        using var visible = await bob.GetAsync("/api/repos/foreign/review/runs/foreign-outcome/report",
            TestContext.Current.CancellationToken);
        visible.EnsureSuccessStatusCode();
        Assert.Contains("foreign-private-marker",
            await visible.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var operations = new (string Method, string Suffix)[]
        {
            ("GET", "/file?path=Foreign.cs"),
            ("GET", "/report?format=json"),
            ("GET", "/review/runs"),
            ("GET", "/review/runs/foreign-outcome"),
            ("GET", "/review/runs/foreign-outcome/report?format=html"),
            ("GET", "/review/runs/compare?baselineId=foreign-outcome&candidateId=foreign-outcome"),
            ("GET", "/review/runs/trend?kind=code&scopeUnitId=boundary-unit&level=file"),
            ("GET", "/review/runs/retention"),
            ("GET", "/review/runs/pins"),
            ("DELETE", "/review/runs/foreign-outcome"),
            ("POST", "/review/runs/foreign-outcome/pause"),
            ("POST", "/review/runs/foreign-outcome/resume"),
            ("POST", "/review/runs/foreign-outcome/pin"),
            ("DELETE", "/review/runs/foreign-outcome/pin"),
            ("GET", "/handover"),
            ("POST", "/handover"),
            ("POST", "/threads"),
            ("POST", "/findings/state"),
            ("GET", "/findings/suppressions"),
            ("POST", "/findings/suppressions"),
            ("DELETE", "/findings/suppressions/foreign-rule"),
        };
        foreach (var (method, suffix) in operations)
        {
            // The explicit foreign route is denied to Alice. The legacy default route is denied
            // to Bob even if query/body fields claim his allowed repository.
            await AssertBoundaryDeniedAsync(alice, method, "/api/repos/foreign" + suffix);
            if (suffix.StartsWith("/report?", StringComparison.Ordinal))
            {
                // This legacy endpoint is intentionally a filtered collection, not default-only.
                var report = await bob.GetFromJsonAsync<JsonElement>("/api" + suffix,
                    TestContext.Current.CancellationToken);
                Assert.Equal("foreign", Assert.Single(report.GetProperty("repositories").EnumerateArray())
                    .GetProperty("id").GetString());
            }
            else
            {
                await AssertBoundaryDeniedAsync(bob, method, "/api" + suffix);
            }
        }
        Assert.Equal(0, outbound.Calls);
        Assert.Empty(new QualityRunReportPinStore(ForeignRepositoryRoot).Load());
    }

    [Fact]
    public async Task Hosted_foreign_run_id_cannot_read_cancel_pause_or_resume_through_an_allowed_route()
    {
        var executor = new ApiRouteCoverageTests.GatedExecutorFactory();
        await using var target = application!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IReviewExecutorFactory>(executor)));
        using var alice = CreateClient(target, "alice", AliceToken);
        using var bob = CreateClient(target, "bob", BobToken);
        try
        {
            using var created = await bob.PostAsJsonAsync("/api/repos/foreign/review",
                new { path = "Foreign.cs", kind = "code", cliType = "test-agent", force = true },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            var runId = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
                .GetProperty("id").GetString()!;
            await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            foreach (var prefix in new[] { "/api", "/api/repos/default" })
            {
                foreach (var (method, suffix) in new[]
                {
                    ("GET", ""), ("DELETE", ""), ("POST", "/pause"), ("POST", "/resume"),
                })
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method),
                        $"{prefix}/review/runs/{runId}{suffix}?repoId=foreign&repositoryId=foreign");
                    if (method == "POST") request.Content = JsonContent.Create(new { repositoryId = "foreign" });
                    using var response = await alice.SendAsync(request, TestContext.Current.CancellationToken);
                    Assert.True(response.StatusCode == HttpStatusCode.NotFound,
                        $"{method} {request.RequestUri} returned {response.StatusCode}");
                }
                var ownRuns = await alice.GetFromJsonAsync<JsonElement>($"{prefix}/review/runs",
                    TestContext.Current.CancellationToken);
                Assert.Empty(ownRuns.GetProperty("runs").EnumerateArray());
            }
            var unchanged = await bob.GetFromJsonAsync<JsonElement>($"/api/repos/foreign/review/runs/{runId}",
                TestContext.Current.CancellationToken);
            Assert.Equal("running", unchanged.GetProperty("state").GetString());
        }
        finally
        {
            executor.Release();
        }
    }

    [Fact]
    public async Task Hosted_report_compare_and_pins_do_not_resolve_ids_from_a_foreign_store()
    {
        using var alice = CreateClient("alice", AliceToken);
        using var bob = CreateClient("bob", BobToken);
        SeedBoundaryOutcome(ForeignRepositoryRoot, "foreign", "foreign-outcome", "Foreign.cs", "foreign-private-marker");
        SeedBoundaryOutcome(RepositoryRoot, "default", "own-outcome", "Sample.cs", "own-marker");
        using var pin = await bob.PostAsJsonAsync("/api/repos/foreign/review/runs/foreign-outcome/pin", new { },
            TestContext.Current.CancellationToken);
        pin.EnsureSuccessStatusCode();

        foreach (var prefix in new[] { "/api", "/api/repos/default" })
        {
            using var export = await alice.GetAsync(
                $"{prefix}/review/runs/foreign-outcome/report?format=json&repoId=foreign",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);
            using var compare = await alice.GetAsync(
                $"{prefix}/review/runs/compare?baselineId=own-outcome&candidateId=foreign-outcome&repoId=foreign",
                TestContext.Current.CancellationToken);
            compare.EnsureSuccessStatusCode();
            var comparison = await compare.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("unavailable", comparison.GetProperty("status").GetString());
            Assert.Equal("found", comparison.GetProperty("baseline").GetProperty("status").GetString());
            Assert.Equal("missing", comparison.GetProperty("candidate").GetProperty("status").GetString());
            Assert.DoesNotContain("foreign-private-marker", comparison.GetRawText(), StringComparison.Ordinal);
            using var foreignPin = await alice.PostAsJsonAsync(
                $"{prefix}/review/runs/foreign-outcome/pin?repoId=foreign", new { repositoryId = "foreign" },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, foreignPin.StatusCode);
            using var unpin = await alice.DeleteAsync(
                $"{prefix}/review/runs/foreign-outcome/pin?repoId=foreign", TestContext.Current.CancellationToken);
            unpin.EnsureSuccessStatusCode();
        }
        Assert.Contains("foreign-outcome", new QualityRunReportPinStore(ForeignRepositoryRoot).Load());
        Assert.Empty(new QualityRunReportPinStore(RepositoryRoot).Load());
    }

    [Fact]
    public async Task Hosted_mutation_body_paths_cannot_select_a_foreign_repository()
    {
        var outbound = new RejectUnexpectedOutboundRequest();
        await using var target = application!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(new HttpClient(outbound))));
        using var alice = CreateClient(target, "alice", AliceToken);
        var foreignPath = "../foreign/Foreign.cs";
        foreach (var suffix in new[] { "/findings/state", "/findings/suppressions", "/threads", "/handover" })
        {
            using var response = await alice.PostAsJsonAsync("/api/repos/default" + suffix + "?repoId=foreign", new
            {
                repositoryId = "foreign",
                repoId = "foreign",
                path = foreignPath,
                filePath = foreignPath,
                kind = "code",
                state = "accepted",
                fingerprint = "sha256:" + new string('a', 64),
                author = "boundary-fixture",
                reason = "boundary fixture",
                line = 1,
                body = "boundary fixture",
                findingSummary = "boundary fixture",
                findingText = "boundary fixture",
                reviewKind = "code",
                metaReference = ".quality/reviews/test.review-meta.code.json#finding",
            }, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"{suffix} returned {response.StatusCode}");
            Assert.DoesNotContain(ForeignRepositoryRoot,
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        Assert.Equal(0, outbound.Calls);
    }

    private static async Task AssertBoundaryDeniedAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = JsonContent.Create(new
        {
            repoId = "foreign",
            repositoryId = "foreign",
            path = "Foreign.cs",
            filePath = "Foreign.cs",
            findingSummary = "boundary fixture",
            findingText = "boundary fixture",
            reviewKind = "code",
            metaReference = ".quality/reviews/test.review-meta.code.json#finding",
        });
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} returned {response.StatusCode}");
        Assert.Equal("Repository not found", await TitleOf(response));
        Assert.DoesNotContain("foreign-private-marker",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static void SeedBoundaryOutcome(string repositoryRoot, string repositoryId, string runId,
        string path, string summary)
    {
        var at = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var run = new QualityRunIdentity(runId, 1, repositoryId, "Boundary fixture", "code", "boundary-unit",
            "file", path, "done", "complete", at, at, at, "fixture-model", "high", "test-agent", false);
        var target = new QualityRunSubjectTarget("boundary-unit", path, path, "sha256:" + new string('a', 64));
        var document = new QualityRunReportDocument(QualityRunReportJson.SchemaId, 1, run,
            new QualityRunSubject(QualityRunReportJson.SubjectManifestHash([target]), [target]),
            new QualityRunExecution(1, 0, 0, 0, 0, "done", [],
                new QualityRunUsage(1, 1, 1, 0, 0, 1, 0m, "USD", "priced", null, null, null),
                new QualityRunCap(null, null, "not-configured", null), null),
            [new QualityRunObservation("boundary-unit", "file", path, "done", true, null, null, at, null,
                "fixture-provider-run", new QualityRunGrade(90, "A", "Fixture"), summary, [])],
            new QualityRunDelta("unavailable", null, "Fixture", [], [], [], []),
            new QualityRunSummary(90, "A", new QualityRunFindingCounts(0,
                new Dictionary<string, int>(), new Dictionary<string, int>()), null, null));
        new QualityRunReportStore(repositoryRoot).Save(document);
    }
}
