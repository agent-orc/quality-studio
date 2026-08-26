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
            Assert.All(inventory.Findings, finding =>
            {
                Assert.Equal(FindingSourceKind.Deterministic, finding.Source?.Kind);
                Assert.Equal(BoundaryInventorySensor.SensorId, finding.Source?.SensorId);
            });
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
    public async Task Bounded_inventory_reports_partial_coverage_and_continues_without_overwriting_repository_truth()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-bounded-").FullName;
        try
        {
            for (var index = 0; index < 120; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"route-{index:D3}.ts"),
                    $"app.get('/route-{index}', handler);",
                    TestContext.Current.CancellationToken);
            }
            var inventoryPath = Path.Combine(root, BoundaryInventorySensor.InventoryRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(inventoryPath)!);
            await File.WriteAllTextAsync(inventoryPath, "complete-inventory-must-survive",
                TestContext.Current.CancellationToken);
            var sensor = new BoundaryInventorySensor();
            var first = await sensor.InventoryAsync(new SensorScanRequest(
                root,
                Configuration: new Dictionary<string, string>
                {
                    [BoundaryInventorySensor.MaxFilesConfigurationKey] = "50",
                }), TestContext.Current.CancellationToken);

            Assert.False(first.Scan.Complete);
            Assert.Equal("bounded", first.Scan.Mode);
            Assert.Equal(120, first.Scan.DiscoveredFiles);
            Assert.Equal(50, first.Scan.ScannedFiles);
            Assert.Equal(70, first.Scan.OmittedFiles);
            Assert.NotNull(first.Scan.ContinuationToken);
            Assert.Contains("maxFiles", first.Scan.PartialReason, StringComparison.Ordinal);
            Assert.Equal("complete-inventory-must-survive",
                await File.ReadAllTextAsync(inventoryPath, TestContext.Current.CancellationToken));

            var result = await sensor.RunAsync(new SensorScanRequest(
                root,
                Configuration: new Dictionary<string, string>
                {
                    [BoundaryInventorySensor.MaxFilesConfigurationKey] = "50",
                    [BoundaryInventorySensor.ContinuationTokenConfigurationKey] = first.Scan.ContinuationToken!,
                },
                PersistMetadata: false), TestContext.Current.CancellationToken);

            Assert.False(result.Complete);
            Assert.Equal("route-099.ts", result.ContinuationToken);
            Assert.Contains("starts after continuation token", result.PartialReason, StringComparison.Ordinal);

            var collected = await new DeterministicEvidenceCollector(new SensorRegistry([sensor])).CollectAsync(
                root,
                [new ReviewSensorConfiguration(BoundaryInventorySensor.SensorId, new Dictionary<string, string>
                {
                    [BoundaryInventorySensor.MaxFilesConfigurationKey] = "50",
                })],
                TestContext.Current.CancellationToken);
            var evidence = Assert.Single(collected);
            Assert.True(evidence.Available);
            Assert.False(evidence.Complete);
            Assert.NotEmpty(evidence.Findings);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Large_mixed_tree_is_scanned_once_per_source_instead_of_once_per_route()
    {
        const int frontendFiles = 1269;
        const int routes = 500;
        var root = Directory.CreateTempSubdirectory("quality-studio-boundaries-large-").FullName;
        try
        {
            var frontend = Directory.CreateDirectory(Path.Combine(root, "frontend", "src")).FullName;
            var filler = string.Join('\n', Enumerable.Range(0, 80).Select(index => $"// filler {index}"));
            for (var index = 0; index < frontendFiles; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(frontend, $"client-{index:D4}.ts"),
                    $"{filler}\nhttp.get('/api/items/{index % routes}');",
                    TestContext.Current.CancellationToken);
            }
            var program = new StringBuilder("var app = WebApplication.Create();\n");
            for (var index = 0; index < routes; index++)
                program.AppendLine($"app.MapGet(\"/api/items/{index}\", () => Results.Ok());");
            program.AppendLine("app.Run();");
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), program.ToString(),
                TestContext.Current.CancellationToken);
            var attributes = new StringBuilder();
            for (var index = 0; index < 250; index++)
            {
                attributes.AppendLine("[Fact]");
                attributes.AppendLine($"public async Task Attribute_heavy_method_{index}() => await Task.CompletedTask;");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "AttributeHeavy.cs"), attributes.ToString(),
                TestContext.Current.CancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(inventory.Scan.Complete, inventory.Scan.PartialReason);
            Assert.Equal(frontendFiles + 2, inventory.Scan.ScannedFiles);
            Assert.Equal(routes, inventory.Entries.Count(entry => entry.Kind == "http"));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Large synthetic tree took {stopwatch.Elapsed}.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed record Widget(string Name);

}
