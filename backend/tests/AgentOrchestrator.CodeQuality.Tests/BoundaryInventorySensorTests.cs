using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class BoundaryInventorySensorTests
{
    [Fact]
    public async Task AspNet_inventory_derives_middleware_configuration_and_mechanical_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-dotnet-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                var builder = WebApplication.CreateBuilder(args);
                builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
                builder.Services.AddCors(options => options.AddPolicy("public", policy => policy.AllowAnyOrigin()));
                builder.Services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => null!));
                var app = builder.Build();
                app.UseCors("public");
                app.UseExceptionHandler(errorApp => errorApp.Run(context =>
                    Results.Problem(detail: exception.Message).ExecuteAsync(context)));
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/api") && security.Authenticate(context) is null) return;
                    if (!identity.CanAccess(repositoryId)) return;
                    await next();
                });
                app.UseRateLimiter();
                app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
                app.MapGet("/api/file", ReadFile);
                app.Run();

                static IResult ReadFile(string path)
                {
                    var content = File.ReadAllText(path);
                    return Results.Ok(content);
                }

                [ApiController]
                [Route("api/widgets")]
                [Authorize]
                public sealed class WidgetsController : ControllerBase
                {
                    [HttpPost("{id}")]
                    [EnableRateLimiting("write")]
                    public IActionResult Update(string id, Widget request) => Ok(request);
                }
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            var api = Assert.Single(inventory.Entries, entry => entry.Name == "GET /api/file");
            Assert.Equal("authenticated", api.Reachability.Value);
            Assert.Equal("required", api.Authentication.Value);
            Assert.Equal("repository-scoped", api.Authorization.Value);
            Assert.Equal("global", api.RateLimit.Value);
            Assert.Equal("global", api.SizeLimit.Value);
            Assert.Contains("filesystem-read", api.SideEffects);
            Assert.Contains(api.Inputs, input => input.Name == "path" && input.Source == "query");
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/request-to-system-sink");
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/permissive-cors");
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/exception-detail-response");
            Assert.Contains(inventory.Findings, finding =>
                finding.RuleId == "boundary/missing-authorization" &&
                finding.Description.Contains("GET /health", StringComparison.Ordinal));
            var mvc = Assert.Single(inventory.Entries, entry => entry.Name == "POST /api/widgets/{id}");
            Assert.Equal("required", mvc.Authentication.Value);
            Assert.Equal("policy", mvc.RateLimit.Value);
            Assert.Contains(mvc.Inputs, input => input.Name == "id" && input.Source == "route");
            Assert.Contains(mvc.Inputs, input => input.Name == "request" && input.Source == "body");

            var persisted = BoundaryInventorySensor.InventoryPathFor(root);
            Assert.True(File.Exists(persisted));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(persisted, TestContext.Current.CancellationToken));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Unknown_static_file_authentication_is_not_a_critical_unauthenticated_mutation()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-static-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                var app = WebApplication.Create();
                app.UseStaticFiles();
                app.Run();
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

            var entry = Assert.Single(inventory.Entries, entry => entry.Kind == "static-files");
            Assert.Equal("unknown", entry.Authentication.Value);
            Assert.Contains("filesystem-read", entry.SideEffects);
            Assert.DoesNotContain(inventory.Findings, finding => finding.Severity == FindingSeverity.Critical);
            Assert.DoesNotContain(inventory.Findings, finding => finding.RuleId == "boundary/unauthenticated-side-effect");
            var unverified = Assert.Single(inventory.Findings,
                finding => finding.RuleId == "boundary/unverified-side-effect-authorization");
            Assert.Equal(FindingSeverity.Medium, unverified.Severity);
            // The surface remains visible for confinement review; read-only does not mean safe.
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/request-to-system-sink");
        }
        finally { TemporaryDirectory.Delete(root); }
    }

    [Fact]
    public async Task Missing_endpoint_authentication_evidence_remains_unknown_even_with_external_policy_configuration()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-auth-unknown-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddAuthorization(options => options.FallbackPolicy =
                    new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                var app = builder.Build();
                app.UseAuthorization();
                app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
                app.MapPost("/run", () => Process.Start("worker"));
                app.Run();
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

            var health = Assert.Single(inventory.Entries, entry => entry.Name == "GET /health");
            Assert.Equal("unknown", health.Authentication.Value);
            Assert.Equal("unknown", health.Authorization.Value);
            var auth = Assert.Single(inventory.Findings, finding =>
                finding.RuleId == "boundary/missing-authorization" && finding.Description.Contains("GET /health", StringComparison.Ordinal));
            Assert.Equal(FindingSeverity.Medium, auth.Severity);
            Assert.DoesNotContain(inventory.Findings, finding => finding.RuleId == "boundary/unauthenticated-side-effect");
            var effect = Assert.Single(inventory.Findings,
                finding => finding.RuleId == "boundary/unverified-side-effect-authorization");
            Assert.Equal(FindingSeverity.High, effect.Severity);
        }
        finally { TemporaryDirectory.Delete(root); }
    }

    [Fact]
    public async Task Explicit_anonymous_process_execution_still_receives_a_critical_finding()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-auth-none-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                var app = WebApplication.Create();
                var group = app.MapGroup("/jobs").RequireAuthorization();
                group.MapPost("/public", () => Process.Start("worker")).AllowAnonymous();
                group.MapPost("/private", () => Process.Start("worker"));
                app.Run();
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

            Assert.Equal("none", Assert.Single(inventory.Entries, entry => entry.Name == "POST /jobs/public").Authentication.Value);
            Assert.Equal("required", Assert.Single(inventory.Entries, entry => entry.Name == "POST /jobs/private").Authentication.Value);
            var finding = Assert.Single(inventory.Findings,
                finding => finding.RuleId == "boundary/unauthenticated-side-effect");
            Assert.Equal(FindingSeverity.Critical, finding.Severity);
            Assert.Contains("POST /jobs/public", finding.Description, StringComparison.Ordinal);
        }
        finally { TemporaryDirectory.Delete(root); }
    }

    [Fact]
    public async Task Delegated_sensor_commands_keep_process_boundaries_and_request_context()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-delegated-").FullName;
        try
        {
            // This source-only fixture is parsed by the analyzer; no process is launched.
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "boundaries", "delegated-process.cs.txt"),
                Path.Combine(root, "Runner.cs"));
            await File.WriteAllTextAsync(Path.Combine(root, "GitleaksSecurityScanner.cs"), """
                public class GitleaksSecurityScanner
                {
                    private readonly ISensorCommandRunner _commandRunner;
                    public Task ScanAsync(string root, string gitleaksPath, string[] arguments, CancellationToken token)
                    {
                        return _commandRunner.RunAsync(gitleaksPath, arguments, root, token);
                    }
                    public Task OtherAsync(ReviewRunner reviewer)
                    {
                        return reviewer.RunAsync("not a subprocess", [], ".");
                    }
                }
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                var request = new SecurityScanRequest(root);
                await scanner.ScanAsync(request);
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

            var processes = inventory.Entries.Where(entry => entry.Kind == "process").ToArray();
            Assert.Equal(2, processes.Length);
            Assert.Contains(processes, entry => entry.Location.Path == "Runner.cs");
            var delegated = Assert.Single(processes, entry => entry.Location.Path == "GitleaksSecurityScanner.cs");
            Assert.Contains("gitleaksPath", delegated.Name, StringComparison.Ordinal);
            Assert.Contains(delegated.Inputs, input => input.Name == "workingDirectory" && input.Source == "request");
            Assert.Contains(delegated.KnownConsumers, consumer => consumer.Path == "Program.cs");
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/request-to-system-sink" &&
                finding.Locations.Any(location => location.Path == "GitleaksSecurityScanner.cs"));
        }
        finally { TemporaryDirectory.Delete(root); }
    }

    [Fact]
    public async Task Adding_an_endpoint_changes_the_repository_owned_inventory()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-diff-").FullName;
        var program = Path.Combine(root, "Program.cs");
        try
        {
            await File.WriteAllTextAsync(program, """
                var app = WebApplication.Create();
                app.MapGet("/first", () => Results.Ok());
                app.Run();
                """, TestContext.Current.CancellationToken);
            var sensor = new BoundaryInventorySensor();
            await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);
            var before = await File.ReadAllTextAsync(
                BoundaryInventorySensor.InventoryPathFor(root), TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(program, """
                var app = WebApplication.Create();
                app.MapGet("/first", () => Results.Ok());
                app.MapPost("/second", (Widget request) => Results.Created("/second/1", request));
                app.Run();
                """, TestContext.Current.CancellationToken);
            await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);
            var after = await File.ReadAllTextAsync(
                BoundaryInventorySensor.InventoryPathFor(root), TestContext.Current.CancellationToken);

            Assert.NotEqual(before, after);
            Assert.DoesNotContain("POST /second", before, StringComparison.Ordinal);
            Assert.Contains("POST /second", after, StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Node_and_browser_boundaries_are_inventoried()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-node-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "server.mjs"), """
                import express from 'express';
                import { spawn } from 'node:child_process';
                const app = express();
                app.use(express.json({ limit: '16kb' }));
                app.get('/items/:id', requireAuth, (req, res) => res.json({ id: req.params.id }));
                app.post('/run', (req, res) => {
                  spawn(req.body.command, []);
                  res.sendStatus(202);
                });
                app.listen(3000, '127.0.0.1');
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "embed.ts"), """
                window.addEventListener('message', event => render(event.data));
                window.parent.postMessage({ type: 'ready' }, '*');
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """
                <iframe src="https://trusted.example/widget"></iframe>
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

            Assert.Contains(inventory.Entries, entry => entry.Name == "GET /items/:id");
            Assert.Contains(inventory.Entries, entry =>
                entry.Kind == "host-listener" && entry.Reachability.Value == "loopback-only");
            Assert.Contains(inventory.Entries, entry =>
                entry.Kind == "process" && entry.Inputs.Any(input => input.Source == "request"));
            Assert.Contains(inventory.Findings, finding =>
                finding.RuleId == "boundary/unverified-side-effect-authorization" &&
                finding.Severity == FindingSeverity.High &&
                finding.Description.Contains("POST /run", StringComparison.Ordinal));
            Assert.Contains(inventory.Entries, entry =>
                entry.Kind == "browser-message" && entry.Direction == "inbound");
            Assert.Contains(inventory.Entries, entry =>
                entry.Kind == "browser-message" && entry.Direction == "outbound" && entry.Name.Contains('*'));
            Assert.Contains(inventory.Entries, entry =>
                entry.Kind == "iframe" && entry.Name.Contains("trusted.example", StringComparison.Ordinal));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task QualityStudio_inventory_covers_registered_routes_loopback_cors_and_gitleaks()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var inventory = await new BoundaryInventorySensor().InventoryAsync(
            new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

        var programPath = Path.Combine(root, "backend", "QualityStudio.Api", "Program.cs");
        var program = await File.ReadAllTextAsync(programPath, TestContext.Current.CancellationToken);
        var registrations = Regex.Matches(program, @"\bapp\.Map(?:Get|Post|Put|Delete|Patch)\s*\(").Count;
        var inventoried = inventory.Entries.Count(entry =>
            entry.Kind == "http" && entry.Location.Path == "backend/QualityStudio.Api/Program.cs");
        Assert.Equal(registrations, inventoried);

        Assert.Contains(inventory.Entries, entry =>
            entry.Kind == "host-listener" &&
            entry.Reachability.Value == "loopback-only" &&
            entry.Name.Contains("127.0.0.1", StringComparison.Ordinal));
        Assert.Contains(inventory.Entries, entry =>
            entry.Kind == "cors-policy" &&
            entry.Authorization.Value == "origin-allowlist" &&
            entry.Evidence.Any(value => value.Contains("localhost:4200", StringComparison.Ordinal)));
        var gitleaks = Assert.Single(inventory.Entries, entry =>
            entry.Kind == "process" &&
            entry.Location.Path.EndsWith("GitleaksSecurityScanner.cs", StringComparison.Ordinal) &&
            entry.Name.Contains("gitleaksPath", StringComparison.Ordinal));
        Assert.Contains(gitleaks.Inputs, input => input.Source == "request");
        Assert.Contains(gitleaks.KnownConsumers, consumer =>
            consumer.Path == "backend/QualityStudio.Api/Program.cs");

        using var generated = JsonDocument.Parse(JsonSerializer.Serialize(inventory,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var schema = SchemaCatalogue.Get("boundary-inventory.v1.schema.json");
        var validation = schema.Evaluate(generated.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }

    [Fact]
    public async Task Stacked_attributes_do_not_trigger_catastrophic_regex_backtracking()
    {
        // QS-95: a class with many stacked bracket attributes above a method - the common
        // xUnit [Theory]/[InlineData(...)] shape found in real test suites - made
        // ControllerRegex's (?:\s*[...]\s*)+ group backtrack exponentially, hanging the whole
        // scan on a single small, ordinary file. Eight stacked attributes alone reproduced a
        // hang beyond ten seconds before the fix; this uses 40 and must resolve in
        // milliseconds.
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-redos-").FullName;
        try
        {
            var attributes = string.Join('\n', Enumerable.Range(0, 40)
                .Select(index => $"    [InlineData(SomeEnum.Value{index}, false, true, Other.Thing{index})]"));
            var content = """
                namespace AgentStudio.Tests;

                public sealed class StackedAttributeTests
                {
                    [Theory]
                __ATTRIBUTES__
                    public void WorkerOutcomeMatrix_DecidesAcceptedLane(int value) { }
                }
                """.Replace("__ATTRIBUTES__", attributes, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, "StackedAttributeTests.cs"), content,
                TestContext.Current.CancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 5_000,
                $"Boundary scan took {stopwatch.ElapsedMilliseconds} ms; a regression to catastrophic " +
                "regex backtracking would make this run for minutes, not milliseconds.");
            Assert.True(inventory.Complete);
            Assert.Empty(inventory.Omissions);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // QS-115: the 15 s budget depends on host load; QS-104, QS-110, QS-111,
    // QS-112 and QS-116 pre-main gates measured 15.7–31 s on Windows.
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Scan_of_many_files_and_routes_stays_within_a_linear_time_budget()
    {
        // QS-95: HostReachability and KnownConsumers used to redo work proportional to the
        // whole repository for every single HTTP route match (a full re-scan of every source
        // file, and a fresh line split plus regex compile for every route/file/line
        // combination), so cost grew with routes * files instead of with repository size. This
        // fixture has enough routes and client-side consumers that a reintroduced O(routes *
        // files) cost would turn a sub-second scan into tens of seconds.
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-scale-").FullName;
        try
        {
            const int fileCount = 200;
            for (var index = 0; index < fileCount; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"Endpoint{index}.cs"), $"""
                    var app = WebApplication.Create();
                    app.MapGet("/api/items/{index}/detail", () => Results.Ok());
                    app.MapPost("/api/items/{index}/update", (Widget request) => Results.Ok());
                    """, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(root, $"consumer{index}.ts"), $$"""
                    export async function load{{index}}() {
                      await fetch(`/api/items/{{index}}/detail`);
                      return axios.post(`/api/items/{{index}}/update`, {});
                    }
                    """, TestContext.Current.CancellationToken);
            }

            var stopwatch = Stopwatch.StartNew();
            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.Equal(fileCount * 2, inventory.Entries.Count(entry => entry.Kind == "http"));
            Assert.True(inventory.Complete, string.Join(", ", inventory.Omissions.Select(omission => omission.Path)));
            Assert.True(stopwatch.ElapsedMilliseconds < 15_000,
                $"Boundary scan of {fileCount * 2} routes across {fileCount * 2} files took " +
                $"{stopwatch.ElapsedMilliseconds} ms; expected roughly linear scaling with repository size.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Exhausted_time_budget_produces_an_honest_partial_result()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-budget-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "First.cs"), """
                var app = WebApplication.Create();
                app.MapGet("/first", () => Results.Ok());
                app.Run();
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Second.cs"), """
                var app2 = WebApplication.Create();
                app2.MapGet("/second", () => Results.Ok());
                app2.Run();
                """, TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false, Configuration: new Dictionary<string, string>
                {
                    [BoundaryInventorySensor.TimeBudgetConfigurationKey] = "0",
                }),
                TestContext.Current.CancellationToken);

            Assert.False(inventory.Complete);
            Assert.NotEmpty(inventory.Omissions);
            Assert.All(inventory.Omissions, omission => Assert.Equal("time-budget-exceeded", omission.Reason));
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/scan-incomplete");

            using var generated = JsonDocument.Parse(JsonSerializer.Serialize(inventory,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var schema = SchemaCatalogue.Get("boundary-inventory.v1.schema.json");
            var validation = schema.Evaluate(generated.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed record Widget(string Name);

}
