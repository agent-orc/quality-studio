using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ApiSecurityTests : IAsyncLifetime
{
    private const string CsrfNonceHeader = "X-Csrf-Nonce";
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
        Assert.Equal(HttpStatusCode.OK, firstHandover.StatusCode);
        using var secondHandover = await bob.PostAsJsonAsync("/api/repos/foreign/handover", handover,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondHandover.StatusCode);
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
        client.DefaultRequestHeaders.Add(CsrfNonceHeader, await IssueNonceAsync(client));
        using var mutation = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mutation.StatusCode);
    }

    [Fact]
    public async Task Local_mode_rejects_cross_origin_mutations()
    {
        var localHost = Path.Combine(testRoot, "local-csrf-host");
        Directory.CreateDirectory(localHost);
        await using var local = new LocalApplication(RepositoryRoot, localHost);

        using var evilOrigin = local.CreateClient();
        evilOrigin.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var blocked = await evilOrigin.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Cross-origin mutation is not permitted", problem.GetProperty("title").GetString());

        using var noOrigin = local.CreateClient();
        noOrigin.DefaultRequestHeaders.Add(CsrfNonceHeader, await IssueNonceAsync(noOrigin));
        using var allowed = await noOrigin.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);

        using var sameOrigin = local.CreateClient();
        sameOrigin.DefaultRequestHeaders.Add(CsrfNonceHeader, await IssueNonceAsync(sameOrigin));
        sameOrigin.DefaultRequestHeaders.Add("Origin", "http://localhost:4200");
        using var sameOriginResponse = await sameOrigin.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, sameOriginResponse.StatusCode);
    }

    [Fact]
    public async Task Local_mode_rejects_mutations_without_a_valid_csrf_nonce()
    {
        var localHost = Path.Combine(testRoot, "local-nonce-host");
        Directory.CreateDirectory(localHost);
        await using var local = new LocalApplication(RepositoryRoot, localHost);

        using var missingNonce = local.CreateClient();
        using var missing = await missingNonce.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        var missingProblem = await missing.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("A valid CSRF nonce is required for local mutations", missingProblem.GetProperty("title").GetString());

        using var forgedNonce = local.CreateClient();
        forgedNonce.DefaultRequestHeaders.Add(CsrfNonceHeader, "not-a-nonce-anyone-issued");
        using var forged = await forgedNonce.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        using var reused = local.CreateClient();
        var nonce = await IssueNonceAsync(reused);
        reused.DefaultRequestHeaders.Add(CsrfNonceHeader, nonce);
        using var first = await reused.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        using var second = await reused.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "code", model = "not-in-catalogue",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Csrf_nonce_endpoint_is_local_only()
    {
        var localHost = Path.Combine(testRoot, "local-nonce-endpoint-host");
        Directory.CreateDirectory(localHost);
        await using var local = new LocalApplication(RepositoryRoot, localHost);
        using var localClient = local.CreateClient();
        using var localResponse = await localClient.GetAsync("/api/security/csrf-nonce", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, localResponse.StatusCode);
        var localBody = await localResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(localBody.GetProperty("nonce").GetString()!.Length >= 32);

        using var hosted = CreateClient("alice", AliceToken);
        using var hostedResponse = await hosted.GetAsync("/api/security/csrf-nonce", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hostedResponse.StatusCode);
    }

    [Fact]
    public async Task Repository_item_mutations_require_registrar_privilege_not_mere_access()
    {
        var update = new
        {
            displayName = "Default (attempted rename)",
            rootPath = RepositoryRoot,
            enabledReviewKinds = new[] { "code" },
        };

        using var alice = CreateClient("alice", AliceToken);
        using var put = await alice.PutAsJsonAsync("/api/repos/default", update, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var putProblem = await put.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Repository configuration changes are not permitted", putProblem.GetProperty("title").GetString());

        using var delete = await alice.DeleteAsync("/api/repos/default", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        using var admin = CreateClient("admin", AdminToken);
        using var adminPut = await admin.PutAsJsonAsync("/api/repos/default", update, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, adminPut.StatusCode);
    }

    [Fact]
    public async Task Command_backed_sensor_configuration_is_rejected_unless_explicitly_allowed()
    {
        var withCommand = new
        {
            displayName = "Default",
            rootPath = RepositoryRoot,
            enabledReviewKinds = new[] { "code" },
            sensors = new[]
            {
                new
                {
                    id = "sarif",
                    enabled = true,
                    configuration = new Dictionary<string, string> { ["command"] = "pwsh -Command notepad.exe" },
                },
            },
        };

        using var admin = CreateClient("admin", AdminToken);
        using var rejected = await admin.PutAsJsonAsync("/api/repos/default", withCommand, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Command-backed analyzer configuration is disabled", problem.GetProperty("title").GetString());

        var permissiveHost = Path.Combine(testRoot, "permissive-host");
        Directory.CreateDirectory(permissiveHost);
        WriteRegistry(permissiveHost);
        await using var permissive = new HostedApplication(RepositoryRoot, ForeignRepositoryRoot, permissiveHost,
            spendRequestsPerMinute: 100, allowCommandBackedAnalyzers: true);
        using var permissiveAdmin = CreateClient(permissive, "admin", AdminToken);
        using var accepted = await permissiveAdmin.PutAsJsonAsync("/api/repos/default", withCommand, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Legacy_persisted_command_backed_sensor_configuration_is_blocked_at_scan_time()
    {
        var legacyHost = Path.Combine(testRoot, "legacy-command-host");
        Directory.CreateDirectory(legacyHost);
        WriteRegistryWithCommandBackedSensor(legacyHost);
        await using var legacyApplication = new HostedApplication(
            RepositoryRoot, ForeignRepositoryRoot, legacyHost, spendRequestsPerMinute: 100);

        using var admin = CreateClient(legacyApplication, "admin", AdminToken);
        using var response = await admin.PostAsync(
            "/api/repos/default/sensors/sarif/scan", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal(
            "Command-backed analyzer configuration is disabled; set " +
            "QualityStudio:Security:AllowCommandBackedAnalyzers to run a configured 'command'.",
            result.GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public async Task Sensor_scans_share_the_spend_rate_limit()
    {
        var rateHost = Path.Combine(testRoot, "sensor-rate-host");
        Directory.CreateDirectory(rateHost);
        WriteRegistry(rateHost);
        await using var rateApplication = new HostedApplication(
            RepositoryRoot, ForeignRepositoryRoot, rateHost, spendRequestsPerMinute: 1);

        using var alice = CreateClient(rateApplication, "alice", AliceToken);
        using var firstScan = await alice.PostAsync("/api/sensors/boundaries/scan", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstScan.StatusCode);
        using var secondScan = await alice.PostAsync("/api/sensors/boundaries/scan", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondScan.StatusCode);
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

    private static async Task<string> IssueNonceAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/security/csrf-nonce",
            TestContext.Current.CancellationToken);
        return body.GetProperty("nonce").GetString()!;
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

    /// <summary>
    /// Writes a registry file with a "command" already present in a persisted sensor configuration,
    /// bypassing <c>RepositoryRegistry</c>'s create/update-time validation entirely. This models a
    /// repository record written before the command-backed-analyzer gate existed (or written while
    /// <c>AllowCommandBackedAnalyzers</c> was enabled and never cleaned up), which the runtime kill
    /// switch in the sensors themselves must still refuse to execute.
    /// </summary>
    private void WriteRegistryWithCommandBackedSensor(string hostRoot)
    {
        var path = Path.Combine(hostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", RepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" },
                new[]
                {
                    new RepositorySensorConfiguration("sarif", Enabled: true,
                        Configuration: new Dictionary<string, string>
                        {
                            ["command"] = "pwsh -Command notepad.exe",
                            ["reportPath"] = ".quality/analyzers/legacy.sarif",
                        }),
                }),
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

    private sealed class HostedApplication(string root, string foreignRoot, string contentRoot, int spendRequestsPerMinute,
        bool allowCommandBackedAnalyzers = false) : WebApplicationFactory<Program>
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
                    ["QualityStudio:Security:AllowCommandBackedAnalyzers"] = allowCommandBackedAnalyzers.ToString(),
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
