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

    [Fact]
    public async Task Bounded_scan_reports_partial_coverage_and_does_not_replace_repository_inventory()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-bounded-").FullName;
        try
        {
            for (var index = 0; index < 30; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"route-{index:D3}.cs"),
                    $"var app = WebApplication.Create(); app.MapGet(\"/route-{index}\", () => Results.Ok());",
                    TestContext.Current.CancellationToken);
            }

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string> { ["maxFiles"] = "5" }),
                TestContext.Current.CancellationToken);

            Assert.False(inventory.Coverage.Complete);
            Assert.Equal("bounded", inventory.Coverage.Mode);
            Assert.Equal(30, inventory.Coverage.FilesDiscovered);
            Assert.Equal(5, inventory.Coverage.FilesScanned);
            Assert.Equal(25, inventory.Coverage.FilesOmitted);
            Assert.Equal(5, inventory.Entries.Count(entry => entry.Kind == "http"));
            Assert.DoesNotContain(inventory.Findings, finding => finding.RuleId == "boundary/missing-authorization");
            Assert.False(File.Exists(Path.Combine(root, BoundaryInventorySensor.InventoryRelativePath)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Incremental_scan_is_explicitly_partial_and_keeps_positive_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-incremental-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "selected.cs"), """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddCors(options => options.AddPolicy("public", policy => policy.AllowAnyOrigin()));
                var app = builder.Build();
                app.MapGet("/selected", () => Results.Ok());
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "omitted.cs"),
                "var app = WebApplication.Create(); app.MapPost(\"/omitted\", () => Results.Ok());",
                TestContext.Current.CancellationToken);

            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, IncludedPaths: ["selected.cs"]),
                TestContext.Current.CancellationToken);

            Assert.False(inventory.Coverage.Complete);
            Assert.Equal("incremental", inventory.Coverage.Mode);
            Assert.Equal(1, inventory.Coverage.FilesScanned);
            Assert.Contains(inventory.Entries, entry => entry.Name == "GET /selected");
            Assert.DoesNotContain(inventory.Entries, entry => entry.Name.Contains("omitted", StringComparison.Ordinal));
            Assert.Contains(inventory.Findings, finding => finding.RuleId == "boundary/permissive-cors");
            Assert.DoesNotContain(inventory.Findings, finding => finding.RuleId == "boundary/missing-authorization");
            Assert.False(File.Exists(Path.Combine(root, BoundaryInventorySensor.InventoryRelativePath)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Security_evidence_marks_incremental_boundary_coverage_unavailable_instead_of_clean()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-security-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "subject.cs"),
                "var value = 42;", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "other.cs"),
                "var app = WebApplication.Create(); app.MapPost(\"/other\", () => Results.Ok());",
                TestContext.Current.CancellationToken);
            var collector = new SecurityEvidenceCollector(
                new SensorRegistry([new BoundaryInventorySensor()]));

            var evidence = await collector.CollectAsync(
                root,
                ["subject.cs"],
                [new ReviewSensorConfiguration("boundaries")],
                TestContext.Current.CancellationToken);

            Assert.Equal(SecurityEvidenceVerdict.Unavailable, evidence.Verdict);
            var sensor = Assert.Single(evidence.Sensors);
            Assert.True(sensor.Available);
            Assert.Equal(SecurityEvidenceVerdict.Unavailable, sensor.Verdict);
            Assert.False(Assert.IsType<SensorScanCoverage>(sensor.Coverage).Complete);
            Assert.Contains("explicitly included paths", sensor.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Large_synthetic_tree_scales_without_route_times_file_rescans()
    {
        const int sourceFileCount = 1500;
        const int routeCount = 300;
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-large-").FullName;
        try
        {
            var routes = string.Join('\n', Enumerable.Range(0, routeCount)
                .Select(index => $"app.MapGet(\"/api/items/{index}\", () => Results.Ok());"));
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"),
                "var app = WebApplication.Create();\n" + routes,
                TestContext.Current.CancellationToken);
            var filler = string.Join('\n', Enumerable.Range(0, 100).Select(index => $"export const filler{index} = {index};"));
            var attributeOnlyCSharp = string.Join('\n', Enumerable.Range(0, 100)
                .Select(index => $"[Fact] public void Case{index}() {{ }}"));
            for (var index = 0; index < sourceFileCount; index++)
            {
                var directory = Directory.CreateDirectory(Path.Combine(root, "src", $"part-{index / 50:D2}"));
                var fileName = index < 100 ? $"attributes-{index:D4}.cs" : $"client-{index:D4}.ts";
                var content = index < 100
                    ? attributeOnlyCSharp
                    : $"client.get('/api/items/{index % routeCount}');\n{filler}";
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, fileName), content,
                    TestContext.Current.CancellationToken);
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(inventory.Coverage.Complete);
            Assert.Equal(sourceFileCount + 1, inventory.Coverage.FilesScanned);
            Assert.Equal(routeCount, inventory.Entries.Count(entry => entry.Kind == "http"));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Large boundary scan took {stopwatch.Elapsed.TotalSeconds:F2}s.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed record Widget(string Name);

}
