using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ApiSecurityTests : IAsyncLifetime
{
    private const string AliceToken = "alice-test-credential";
    private const string BobToken = "bob-test-credential";
    private const string AdminToken = "admin-test-credential";
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-security-tests", Guid.NewGuid().ToString("N"));
    private string RepositoryRoot => Path.Combine(testRoot, "default");
    private string ForeignRepositoryRoot => Path.Combine(testRoot, "foreign");
    private string OutsideRoot => Path.Combine(testRoot, "outside");
    private string HostRoot => Path.Combine(testRoot, "host");
    private HostedApplication? application;

    [Fact]
    public async Task Hosted_mode_refuses_unauthenticated_mutation_and_requires_matching_client_id()
    {
        using var anonymous = CreateClient();
        using var unauthenticated = await anonymous.PostAsJsonAsync("/api/review",
            new { path = "Sample.cs", kind = "code" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var missingClientId = CreateClient("alice", AliceToken, includeClientId: false);
        using var missingHeader = await missingClientId.PostAsJsonAsync("/api/review",
            new { path = "Sample.cs", kind = "code" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missingHeader.StatusCode);

        using var wrongClientId = CreateClient("bob", AliceToken);
        using var mismatch = await wrongClientId.PostAsJsonAsync("/api/review",
            new { path = "Sample.cs", kind = "code" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatch.StatusCode);
    }

    [Fact]
    public async Task Hosted_identity_cannot_list_or_read_a_foreign_repository()
    {
        using var alice = CreateClient("alice", AliceToken);
        using var file = await alice.GetAsync("/api/repos/foreign/file?path=Foreign.cs",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);

        var list = await alice.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        var repository = Assert.Single(list.GetProperty("repositories").EnumerateArray());
        Assert.Equal(RepositoryRegistry.DefaultRepositoryId, repository.GetProperty("id").GetString());
        Assert.DoesNotContain(ForeignRepositoryRoot,
            await file.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var aliceReport = await alice.GetFromJsonAsync<JsonElement>("/api/report",
            TestContext.Current.CancellationToken);
        Assert.Equal("default", Assert.Single(aliceReport.GetProperty("repositories").EnumerateArray())
            .GetProperty("id").GetString());
        Assert.DoesNotContain(ForeignRepositoryRoot, aliceReport.GetRawText(), StringComparison.Ordinal);

        using var bob = CreateClient("bob", BobToken);
        var bobReport = await bob.GetFromJsonAsync<JsonElement>("/api/report",
            TestContext.Current.CancellationToken);
        Assert.Equal("foreign", Assert.Single(bobReport.GetProperty("repositories").EnumerateArray())
            .GetProperty("id").GetString());
        Assert.DoesNotContain(RepositoryRoot, bobReport.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traversal_and_paths_outside_allowed_roots_are_refused_without_path_disclosure()
    {
        using var alice = CreateClient("alice", AliceToken);
        var traversalPath = $"../{Path.GetFileName(ForeignRepositoryRoot)}/Foreign.cs";
        using var traversal = await alice.GetAsync($"/api/file?path={Uri.EscapeDataString(traversalPath)}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

        using var admin = CreateClient("admin", AdminToken);
        using var registration = await admin.PostAsJsonAsync("/api/repos", new
        {
            id = "outside",
            displayName = "Outside",
            rootPath = OutsideRoot,
            enabledReviewKinds = new[] { "code" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, registration.StatusCode);
        var problem = await registration.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Repository path is outside the allowed roots", problem.GetProperty("title").GetString());
        Assert.DoesNotContain(OutsideRoot, problem.GetRawText(), StringComparison.Ordinal);

        using var globalInputs = await admin.PostAsJsonAsync("/api/repos", new
        {
            id = "escaped-inputs",
            displayName = "Escaped inputs",
            rootPath = RepositoryRoot,
            globalInputsDirectory = OutsideRoot,
            enabledReviewKinds = new[] { "code" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, globalInputs.StatusCode);
        var inputProblem = await globalInputs.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Global inputs directory is outside the allowed roots", inputProblem.GetProperty("title").GetString());
        Assert.DoesNotContain(OutsideRoot, inputProblem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_administration_requires_registrar_and_analyzers_use_host_owned_profiles()
    {
        var registration = new
        {
            displayName = "Default",
            rootPath = RepositoryRoot,
            enabledReviewKinds = new[] { "code", "security" },
            sensors = new object[]
            {
                new { id = "sarif", enabled = true, profileId = "trusted-sarif" },
            },
        };
        using var alice = CreateClient("alice", AliceToken);
        using var forbiddenUpdate = await alice.PutAsJsonAsync(
            "/api/repos/default", registration, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenUpdate.StatusCode);
        using var forbiddenArchive = await alice.DeleteAsync(
            "/api/repos/default", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenArchive.StatusCode);

        using var admin = CreateClient("admin", AdminToken);
        using var commandInjection = await admin.PutAsJsonAsync("/api/repos/default", new
        {
            displayName = "Default",
            rootPath = RepositoryRoot,
            enabledReviewKinds = new[] { "security" },
            sensors = new object[]
            {
                new
                {
                    id = "sarif",
                    enabled = true,
                    configuration = new
                    {
                        command = "pwsh -Command Get-ChildItem Env:",
                        reportPath = ".quality/analyzers/result.sarif",
                    },
                },
            },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, commandInjection.StatusCode);

        using var accepted = await admin.PutAsJsonAsync(
            "/api/repos/default", registration, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var stored = await accepted.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var sensor = Assert.Single(stored.GetProperty("sensors").EnumerateArray());
        Assert.Equal("trusted-sarif", sensor.GetProperty("profileId").GetString());
        Assert.StartsWith("dotnet ", sensor.GetProperty("configuration").GetProperty("command").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hosted_mode_protects_quota_data_and_rejects_unsafe_model_ids()
    {
        using var anonymous = CreateClient();
        using var quotas = await anonymous.GetAsync("/api/quotas", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, quotas.StatusCode);

        using var alice = CreateClient("alice", AliceToken);
        using var review = await alice.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            model = "../../not-a-runner-model",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, review.StatusCode);
    }

    [Fact]
    public async Task Review_and_handover_have_per_client_spend_rate_limits()
    {
        var rateHost = Path.Combine(testRoot, "rate-host");
        Directory.CreateDirectory(rateHost);
        WriteRegistry(rateHost);
        await using var rateApplication = new HostedApplication(
            RepositoryRoot, ForeignRepositoryRoot, rateHost, spendRequestsPerMinute: 1);

        using var alice = CreateClient(rateApplication, "alice", AliceToken);
        using var firstReview = await alice.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, firstReview.StatusCode);
        using var secondReview = await alice.PostAsJsonAsync("/api/review",
            new { path = "Sample.cs", kind = "code" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondReview.StatusCode);

        using var bob = CreateClient(rateApplication, "bob", BobToken);
        var handover = new
        {
            findingSummary = "Confine the path",
            filePath = "Foreign.cs",
            findingText = "Keep the path confined.",
            reviewKind = "security",
            metaReference = ".quality/reviews/example.review-meta.security.json#finding",
        };
        using var firstHandover = await bob.PostAsJsonAsync("/api/repos/foreign/handover", handover,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, firstHandover.StatusCode);
        using var secondHandover = await bob.PostAsJsonAsync("/api/repos/foreign/handover", handover,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondHandover.StatusCode);

        using var admin = CreateClient(rateApplication, "admin", AdminToken);
        using var firstSensorScan = await admin.PostAsync(
            "/api/sensors/boundaries/scan", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstSensorScan.StatusCode);
        using var secondSensorScan = await admin.PostAsync(
            "/api/sensors/boundaries/scan", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondSensorScan.StatusCode);
    }

    [Fact]
    public async Task Local_mode_is_explicitly_credential_free()
    {
        var localHost = Path.Combine(testRoot, "local-host");
        Directory.CreateDirectory(localHost);
        await using var local = new LocalApplication(RepositoryRoot, localHost);
        using var client = local.CreateClient();

        using var read = await client.GetAsync("/api/file?path=Sample.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var mutation = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mutation.StatusCode);
    }

    [Fact]
    public async Task Local_mutations_require_allowed_origin_and_session_nonce()
    {
        var localHost = Path.Combine(testRoot, "csrf-host");
        Directory.CreateDirectory(localHost);
        await using var local = new LocalApplication(RepositoryRoot, localHost);
        using var unprotected = ((WebApplicationFactory<Program>)local).CreateClient();
        using var missingProtection = await unprotected.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, missingProtection.StatusCode);

        using var protectedClient = local.CreateClient();
        protectedClient.DefaultRequestHeaders.Remove("Origin");
        protectedClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://attacker.invalid");
        using var crossOrigin = await protectedClient.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);

        using var tamperedClient = local.CreateClient();
        tamperedClient.DefaultRequestHeaders.Remove(ApiSecurity.AntiCsrfHeader);
        tamperedClient.DefaultRequestHeaders.TryAddWithoutValidation(ApiSecurity.AntiCsrfHeader, "tampered");
        using var tamperedNonce = await tamperedClient.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, tamperedNonce.StatusCode);
    }

    [Fact]
    public void Local_mode_rejects_non_loopback_bindings()
    {
        var options = Options.Create(new RepositoryOptions
        {
            AllowedOrigins = ["http://localhost:4200"],
            Security = new ApiSecurityOptions { Mode = ApiSecurityOptions.LocalMode },
        });
        var security = new ApiSecurity(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["urls"] = "http://0.0.0.0:5127" }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() => security.ValidateLocalBindings(configuration));
        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask InitializeAsync()
    {
        foreach (var directory in new[] { RepositoryRoot, ForeignRepositoryRoot, OutsideRoot, HostRoot })
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.cs"), "namespace Sample; public sealed class Subject;");
        await File.WriteAllTextAsync(Path.Combine(ForeignRepositoryRoot, "Foreign.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(ForeignRepositoryRoot, "Foreign.cs"), "namespace Foreign; public sealed class Secret;");
        await RunGitAsync(RepositoryRoot);
        await RunGitAsync(ForeignRepositoryRoot);
        await RunGitAsync(OutsideRoot);
        WriteRegistry(HostRoot);
        application = new HostedApplication(RepositoryRoot, ForeignRepositoryRoot, HostRoot, spendRequestsPerMinute: 100);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    private HttpClient CreateClient(string? clientId = null, string? token = null, bool includeClientId = true) =>
        CreateClient(application!, clientId, token, includeClientId);

    private static HttpClient CreateClient(WebApplicationFactory<Program> target, string? clientId = null,
        string? token = null, bool includeClientId = true)
    {
        var client = target.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (includeClientId && clientId is not null) client.DefaultRequestHeaders.Add(ApiSecurity.ClientIdHeader, clientId);
        return client;
    }

    private void WriteRegistry(string hostRoot)
    {
        var path = Path.Combine(hostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", RepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("foreign", "Foreign", ForeignRepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static async Task RunGitAsync(string directory)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class HostedApplication(string root, string foreignRoot, string contentRoot, int spendRequestsPerMinute)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = root,
                    ["QualityStudio:AllowedRoots:1"] = foreignRoot,
                    ["QualityStudio:Security:Mode"] = "Hosted",
                    ["QualityStudio:Security:RequireHttps"] = "true",
                    ["QualityStudio:Security:SpendRequestsPerMinute"] = spendRequestsPerMinute.ToString(),
                    ["QualityStudio:Security:Clients:0:Id"] = "alice",
                    ["QualityStudio:Security:Clients:0:CredentialSha256"] = Hash(AliceToken),
                    ["QualityStudio:Security:Clients:0:Repositories:0"] = "default",
                    ["QualityStudio:Security:Clients:1:Id"] = "bob",
                    ["QualityStudio:Security:Clients:1:CredentialSha256"] = Hash(BobToken),
                    ["QualityStudio:Security:Clients:1:Repositories:0"] = "foreign",
                    ["QualityStudio:Security:Clients:2:Id"] = "admin",
                    ["QualityStudio:Security:Clients:2:CredentialSha256"] = Hash(AdminToken),
                    ["QualityStudio:Security:Clients:2:Repositories:0"] = "*",
                    ["QualityStudio:Security:Clients:2:CanRegisterRepositories"] = "true",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:SensorId"] = "sarif",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:Executable"] = "dotnet",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:Arguments:0"] = "tool",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:Arguments:1"] = "run",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:Arguments:2"] = "--output",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:Arguments:3"] = "{reportPath}",
                    ["QualityStudio:AnalyzerProfiles:trusted-sarif:ReportPath"] = ".quality/analyzers/result.sarif",
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                    ["AgentStudio:DryRun"] = "true",
                }));
        }

        private static string Hash(string credential) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    private sealed class LocalApplication(string root, string contentRoot) : WebApplicationFactory<Program>
    {
        public new HttpClient CreateClient() => LocalApiClient.Create(this);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = root,
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
        }
    }
}
