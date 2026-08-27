using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

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

            var persisted = Path.Combine(root, BoundaryInventorySensor.InventoryRelativePath);
            Assert.True(File.Exists(persisted));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(persisted, TestContext.Current.CancellationToken));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
                Path.Combine(root, BoundaryInventorySensor.InventoryRelativePath),
                TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(program, """
                var app = WebApplication.Create();
                app.MapGet("/first", () => Results.Ok());
                app.MapPost("/second", (Widget request) => Results.Created("/second/1", request));
                app.Run();
                """, TestContext.Current.CancellationToken);
            await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);
            var after = await File.ReadAllTextAsync(
                Path.Combine(root, BoundaryInventorySensor.InventoryRelativePath),
                TestContext.Current.CancellationToken);

            Assert.NotEqual(before, after);
            Assert.DoesNotContain("POST /second", before, StringComparison.Ordinal);
            Assert.Contains("POST /second", after, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
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
                finding.RuleId == "boundary/unauthenticated-side-effect" &&
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
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task QualityStudio_inventory_covers_registered_routes_loopback_cors_and_gitleaks()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var inventory = await new BoundaryInventorySensor().InventoryAsync(
            new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

        var programPath = Path.Combine(root, "src", "QualityStudio.Api", "Program.cs");
        var program = await File.ReadAllTextAsync(programPath, TestContext.Current.CancellationToken);
        var registrations = Regex.Matches(program, @"\bapp\.Map(?:Get|Post|Put|Delete|Patch)\s*\(").Count;
        var inventoried = inventory.Entries.Count(entry =>
            entry.Kind == "http" && entry.Location.Path == "src/QualityStudio.Api/Program.cs");
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
            consumer.Path == "src/QualityStudio.Api/Program.cs");

        using var generated = JsonDocument.Parse(JsonSerializer.Serialize(inventory,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(root, "schemas", "boundary-inventory.v1.schema.json"),
            TestContext.Current.CancellationToken));
        var validation = schema.Evaluate(generated.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }

    // Regression test for N-03: the boundary sensor derived its inventory by rescanning every
    // JS/TS file (and, for reachability, every source file) once per inbound entry, an
    // O(entries x files x lines) scan. On Quality Studio (144 files) that ran in ~1.4s; on a
    // ~1300-file frontend plus a large .NET backend it did not return inside a 300-second client
    // timeout. This synthetic repository is intentionally sized to make that quadratic behavior
    // decisive (100 backend routes x 200 client files x 50 lines = 1,000,000 line checks) while
    // staying well inside default unit-test patience once the scan is actually linear.
    [Fact]
    public async Task Large_synthetic_repository_scans_quickly_and_still_derives_known_consumers()
    {
        const int RouteCount = 100;
        const int JsFileCount = 200;
        const int LinesPerJsFile = 50;

        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-scale-").FullName;
        try
        {
            var program = new StringBuilder("var app = WebApplication.Create();\n");
            for (var route = 0; route < RouteCount; route++)
                program.Append($"app.MapGet(\"/api/resource{route}/items\", () => Results.Ok());\n");
            program.Append("app.Run();\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), program.ToString(),
                TestContext.Current.CancellationToken);

            // Every 10th file genuinely references one of the routes; the rest are filler that
            // inflates the total line count without matching anything, exercising the scan's
            // ability to reject non-matching lines cheaply instead of paying for a regex match.
            for (var file = 0; file < JsFileCount; file++)
            {
                var builder = new StringBuilder();
                for (var line = 0; line < LinesPerJsFile; line++)
                {
                    builder.Append(file % 10 == 0 && line == 5
                        ? $"client.get('/api/resource{file % RouteCount}/items');\n"
                        : $"// filler line {file}-{line} does not mention any api route\n");
                }
                await File.WriteAllTextAsync(Path.Combine(root, $"module-{file}.ts"), builder.ToString(),
                    TestContext.Current.CancellationToken);
            }

            var sensor = new BoundaryInventorySensor();
            var stopwatch = Stopwatch.StartNew();
            var inventory = await sensor.InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
                $"Boundary scan of a {JsFileCount}-file/{RouteCount}-route synthetic repository took " +
                $"{stopwatch.Elapsed}, which indicates known-consumer linking has regressed back to " +
                "O(entries x files x lines).");
            Assert.Null(inventory.Partial);
            Assert.Equal(RouteCount, inventory.Entries.Count(entry => entry.Kind == "http"));
            var linked = Assert.Single(inventory.Entries, entry => entry.Name == "GET /api/resource0/items");
            Assert.Contains(linked.KnownConsumers, consumer => consumer.Path == "module-0.ts");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Exceeded_scan_budget_returns_an_honest_partial_result_instead_of_hanging()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-budget-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                var app = WebApplication.Create();
                app.MapGet("/first", () => Results.Ok());
                app.Run();
                """, TestContext.Current.CancellationToken);
            var configuration = new SensorScanRequest(root, PersistMetadata: false,
                Configuration: new Dictionary<string, string> { ["budgetSeconds"] = "0" });
            var sensor = new BoundaryInventorySensor();

            var inventory = await sensor.InventoryAsync(configuration, TestContext.Current.CancellationToken);

            Assert.True(inventory.Partial);
            Assert.NotNull(inventory.PartialReasons);
            Assert.NotEmpty(inventory.PartialReasons);
            Assert.Contains(inventory.PartialReasons, reason =>
                reason.Contains("scan budget", StringComparison.OrdinalIgnoreCase));
            // A zero budget elapses before the first file is even read, so there is nothing to
            // derive entries from — the honest answer is an empty, clearly-labeled partial scan
            // rather than a stale or fabricated inventory.
            Assert.Empty(inventory.Entries);

            var result = await sensor.RunAsync(configuration, TestContext.Current.CancellationToken);
            Assert.True(result.Available);
            Assert.False(string.IsNullOrWhiteSpace(result.UnavailableReason));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Regression test for a second, independent scaling bug found while verifying the N-03 fix
    // against a real large repository: ControllerRegex/ControllerActionRegex matched a class's
    // attribute prefix with an unbounded, backtracking-prone `(?:...)+`/`(?:...)*` group. A file
    // with many stacked attributes (e.g. a [Theory] test with several [InlineData(...)] cases)
    // but no Controller-suffixed class or [Http*] action forced the engine to try every partition
    // of that attribute run before giving up — a single 4.7KB real-world file like this took
    // ~25 seconds. This is unrelated to file/entry *count*; it is triggered by file *content*.
    [Fact]
    public async Task Stacked_attributes_on_a_non_controller_class_do_not_cause_catastrophic_backtracking()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-redos-").FullName;
        try
        {
            var builder = new StringBuilder("namespace Sample.Tests;\n\npublic sealed class PolicyMatrixTests\n{\n    [Theory]\n");
            for (var index = 0; index < 40; index++)
                builder.Append($"    [InlineData({index}, false, true, \"outcome-{index}\")]\n");
            builder.Append("""
                    public void OutcomeMatrix_DecidesLane(int outcome, bool operatorOverride, bool integrationRequired, string expected)
                    {
                    }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "PolicyMatrixTests.cs"), builder.ToString(),
                TestContext.Current.CancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Scanning a class with 40 stacked attributes and no Controller/[Http*] match took " +
                $"{stopwatch.Elapsed}, which indicates ControllerRegex/ControllerActionRegex has " +
                "regressed back to catastrophic backtracking.");
            Assert.Null(inventory.Partial);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed record Widget(string Name);

}
