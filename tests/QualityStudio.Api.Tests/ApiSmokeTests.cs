using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Quota;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ApiSmokeTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-api-tests", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-api-hosts", Guid.NewGuid().ToString("N"));
    private TestApplication? application;

    [Fact]
    public async Task Tree_returns_derived_hierarchy_and_kind_states()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var project = Assert.Single(json.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.Equal("project", project.GetProperty("level").GetString());
        Assert.True(project.GetProperty("kinds").TryGetProperty("code", out var code));
        Assert.Equal("missing", code.GetProperty("overall").GetString());
        var module = Assert.Single(project.GetProperty("children").EnumerateArray());
        Assert.Equal("Sample", module.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tree_returns_etag_and_honours_conditional_request()
    {
        using var client = application!.CreateClient();
        using var first = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);
        Assert.NotNull(first.Headers.ETag);
        var etag = first.Headers.ETag.Tag;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tree?path=");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var cached = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        Assert.Equal(etag, cached.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Mixed_repository_tree_exposes_typescript_and_can_queue_file_review()
    {
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "frontend", "src", "app"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "frontend", "angular.json"),
            "{\"projects\":{\"frontend\":{\"root\":\"\",\"sourceRoot\":\"src\"}}}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "frontend", "src", "app", "app.component.ts"),
            "@Component({standalone: true}) export class AppComponent {}", TestContext.Current.CancellationToken);
        using var client = application!.CreateClient();

        using var treeResponse = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);
        var tree = await treeResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var nodes = FlattenTree(tree.GetProperty("nodes")).ToArray();
        Assert.Contains(nodes, node => node.GetProperty("path").GetString() == "frontend/src/app/app.component.ts");

        using var review = await client.PostAsJsonAsync("/api/review", new
        {
            path = "frontend/src/app/app.component.ts",
            kind = "code",
            cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, review.StatusCode);
        var accepted = await review.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("frontend/src/app/app.component.ts", accepted.GetProperty("path").GetString());
        Assert.Equal(1, accepted.GetProperty("totalFiles").GetInt32());
    }

    [Fact]
    public async Task Scan_returns_staleness_report()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/scan", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, json.GetProperty("missingCount").GetInt32());
        var file = Assert.Single(json.GetProperty("files").EnumerateArray());
        Assert.Equal("Sample.cs", file.GetProperty("relativePath").GetString());
        Assert.Equal("missing", file.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Handover_dry_run_returns_the_would_be_card()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/handover", new
        {
            findingSummary = "Avoid repeated work",
            filePath = "Sample.cs",
            findingText = "Cache the repeated operation.",
            reviewKind = "performance",
            metaReference = ".quality/reviews/sample.review-meta.performance.json#repeated-work",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Fix: Avoid repeated work in Sample.cs", json.GetProperty("card").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Inputs_lists_resolved_project_inputs_for_each_kind()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/inputs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var code = json.GetProperty("kinds").GetProperty("code");
        var input = Assert.Single(code.GetProperty("inputs").EnumerateArray());
        Assert.Equal("sample-rules", input.GetProperty("id").GetString());
        Assert.Equal("project", input.GetProperty("scope").GetString());
        Assert.Empty(json.GetProperty("kinds").GetProperty("security").GetProperty("inputs").EnumerateArray());
    }

    [Fact]
    public async Task Security_scan_returns_redacted_scan_summary()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/security/scan", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("block", json.GetProperty("verdict").GetString());
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal("gitleaks", json.GetProperty("scanner").GetString());
        var finding = Assert.Single(json.GetProperty("findings").EnumerateArray());
        Assert.Equal("test-rule", finding.GetProperty("ruleId").GetString());
        Assert.Equal("Gitleaks detected a potential secret in Sample.cs at lines 1-1.", finding.GetProperty("description").GetString());
        Assert.Equal("Rotate the credential and remove the token from the repository.", finding.GetProperty("recommendation").GetString());
        Assert.Equal("Sample.cs", finding.GetProperty("path").GetString());
        Assert.False(finding.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Health_returns_ok_for_the_dev_launcher()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("QualityStudio.Api", json.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Usage_returns_filtered_ledger_aggregates_and_recent_entries()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        await UsageLedger.AppendAsync(repositoryRoot, new ReviewUsageEntry("usage-api-run", timestamp, "gpt-5", "codex",
            new TokenUsage(200, 50, 80, 10, 2400), "performance", "file", "Sample.cs"), TestContext.Current.CancellationToken);

        using var client = application!.CreateClient();
        var since = Uri.EscapeDataString(timestamp.AddMinutes(-1).ToString("O"));
        using var response = await client.GetAsync($"/api/usage?since={since}&kind=performance", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, json.GetProperty("runs").GetInt32());
        Assert.Equal(200, json.GetProperty("inputTokens").GetInt64());
        Assert.Equal("gpt-5", Assert.Single(json.GetProperty("byModel").EnumerateArray()).GetProperty("key").GetString());
        Assert.Equal("usage-api-run", Assert.Single(json.GetProperty("recent").EnumerateArray()).GetProperty("runId").GetString());
    }

    [Fact]
    public async Task Quotas_returns_a_clean_empty_report_when_no_provider_data_is_available()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/quotas", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Empty(json.GetProperty("providers").EnumerateArray());
        Assert.True(json.GetProperty("ttlSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Review_endpoint_queues_and_reports_per_file_failure_without_blocking()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = accepted.GetProperty("id").GetString()!;
        Assert.Equal(1, accepted.GetProperty("totalFiles").GetInt32());
        var runDirectory = Path.Combine(repositoryRoot, ".quality", "runs", id);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(runDirectory, "manifest.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("Sample.cs", manifest.RootElement.GetProperty("node").GetProperty("path").GetString());
            var target = Assert.Single(manifest.RootElement.GetProperty("targets").EnumerateArray());
            Assert.Equal(
                await ReviewSubjectHasher.ComputeFileContentHashAsync(
                    Path.Combine(repositoryRoot, "Sample.cs"), TestContext.Current.CancellationToken),
                target.GetProperty("subjectHash").GetString());
        }
        Assert.True(File.Exists(Path.Combine(runDirectory, "progress.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "status.json")));

        JsonElement run = default;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{id}", TestContext.Current.CancellationToken);
            if (run.GetProperty("state").GetString() == "done") break;
        }

        Assert.Equal("done", run.GetProperty("state").GetString());
        Assert.Equal(1, run.GetProperty("failedFiles").GetInt32());
        Assert.Equal("failed", Assert.Single(run.GetProperty("files").EnumerateArray()).GetProperty("state").GetString());
        var list = await client.GetFromJsonAsync<JsonElement>("/api/review/runs", TestContext.Current.CancellationToken);
        Assert.Contains(list.GetProperty("runs").EnumerateArray(), candidate => candidate.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Registry_onboards_and_scopes_a_second_repository()
    {
        var secondRoot = repositoryRoot + "-second";
        Directory.CreateDirectory(secondRoot);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "Second.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "Second.cs"), "namespace Second; public sealed class Marker;", TestContext.Current.CancellationToken);
        await RunGitInDirectoryAsync(secondRoot, "init", "--quiet");

        try
        {
            using var client = application!.CreateClient();
            var create = await client.PostAsJsonAsync("/api/repos", new
            {
                id = "second",
                displayName = "Second repository",
                rootPath = secondRoot,
                globalInputsDirectory = (string?)null,
                inputBudgetCharacters = 8000,
                enabledReviewKinds = new[] { "code", "security" },
            }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var scopedFile = await client.GetAsync("/api/repos/second/file?path=Second.cs", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, scopedFile.StatusCode);
            var file = await scopedFile.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Contains("namespace Second", file.GetProperty("content").GetString());

            using var traversal = await client.GetAsync($"/api/file?path=../{Path.GetFileName(secondRoot)}/Second.cs", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

            var persisted = await File.ReadAllTextAsync(Path.Combine(hostRoot, ".quality-studio", "repositories.json"), TestContext.Current.CancellationToken);
            Assert.Contains("Second repository", persisted);
        }
        finally
        {
            Directory.Delete(secondRoot, true);
        }
    }

    [Fact]
    public async Task Registry_rejects_a_directory_that_is_not_a_git_repository()
    {
        var invalidRoot = repositoryRoot + "-not-git";
        Directory.CreateDirectory(invalidRoot);
        try
        {
            using var client = application!.CreateClient();
            var response = await client.PostAsJsonAsync("/api/repos", new
            {
                displayName = "Invalid repository",
                rootPath = invalidRoot,
                inputBudgetCharacters = 12000,
                enabledReviewKinds = new[] { "code" },
            }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Contains("not a Git repository", problem.GetProperty("detail").GetString());
        }
        finally
        {
            Directory.Delete(invalidRoot, true);
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"), "namespace Sample; public static class Greeter { public static string Hello() => \"hello\"; }");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".quality", "inputs"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".quality", "inputs", "sample.md"),
            "---\nid: sample-rules\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nPrefer explicit names.\n");
        await RunGitAsync("init", "--quiet");
        application = new TestApplication(repositoryRoot, hostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        try
        {
            Directory.Delete(repositoryRoot, true);
            Directory.Delete(hostRoot, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task RunGitAsync(params string[] arguments)
    {
        await RunGitInDirectoryAsync(repositoryRoot, arguments);
    }

    private static IEnumerable<JsonElement> FlattenTree(JsonElement nodes)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            yield return node;
            foreach (var child in FlattenTree(node.GetProperty("children"))) yield return child;
        }
    }

    private static async Task RunGitInDirectoryAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TestApplication(string root, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.AddSingleton<GitleaksSecurityScanner, FakeSecurityScanner>();
            });
        }
    }

    private sealed class FakeSecurityScanner : GitleaksSecurityScanner
    {
        public FakeSecurityScanner() : base(null, null) { }

        public override Task<SecurityScanResult> ScanAsync(SecurityScanRequest request, CancellationToken cancellationToken = default)
        {
            var finding = new SecurityFindingRecord(
                "gitleaks-secret-1",
                "secrets",
                FindingSeverity.High,
                "Hard-coded token",
                "Gitleaks detected a potential secret in Sample.cs at lines 1-1.",
                "Rotate the credential and remove the token from the repository.",
                [new FindingLocation("Sample.cs", new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 12)))],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "test-rule",
                null,
                "Sample.cs",
                Accepted: false);

            var scannedAt = DateTime.UtcNow.ToString("O");
            var report = new SecurityScanReport(
                SecurityVerdict.Block,
                true,
                "gitleaks",
                "8.24.2",
                "repository",
                null,
                null,
                null,
                scannedAt,
                1,
                1,
                0,
                1,
                0,
                0,
                null,
                [finding]);

            var provenance = new SecurityScanProvenance("gitleaks", "8.24.2", "repository", null, null, null, scannedAt);
            var counts = new SecurityScanCounts(1, 1, 0, 1, 0, 0);
            return Task.FromResult(new SecurityScanResult(report, provenance, counts, [finding]));
        }
    }
}
