using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class UsageLedgerTests
{
    // JsonSchema.Net registers each schema's $id globally and refuses a second registration, so
    // the v3 schema is parsed once for every validation in this class.
    private static readonly Lazy<JsonSchema> LedgerV3Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "usage-ledger.v3.schema.json"))));

    [Fact]
    public async Task V3EntriesAttributeEveryOperationToAModelSource()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-v3-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-3", timestamp, "gpt-5.6-luna", "codex",
                new TokenUsage(50, 10, 20, 2, 600), "code", "file", "src/c.ts",
                "review-sweep-3", UsageLedger.CurrentSchemaVersion, ReviewModelSource.PolicyDefault),
                TestContext.Current.CancellationToken);
            // A standalone CLI review has no sweep id but still names its model source.
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-4", timestamp.AddMinutes(1), "gpt-5.6-sol", "codex",
                new TokenUsage(25, 5, 10, 1, 300), "code", "file", "src/d.ts",
                null, UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit),
                TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line);
                var validation = LedgerV3Schema.Value.Evaluate(json.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
                Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
            }

            using (var first = JsonDocument.Parse(lines[0]))
            {
                Assert.Equal("policy-default", first.RootElement.GetProperty("modelSource").GetString());
                Assert.Equal("review-sweep-3", first.RootElement.GetProperty("reviewRunId").GetString());
            }
            using (var second = JsonDocument.Parse(lines[1]))
            {
                Assert.Equal("explicit", second.RootElement.GetProperty("modelSource").GetString());
                Assert.False(second.RootElement.TryGetProperty("reviewRunId", out _));
            }

            // Both v3 shapes are queryable next to the older versions.
            var report = await UsageLedger.QueryAsync(root.FullName, timestamp.AddMinutes(-1), "code",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, report.Runs);
            Assert.Contains(report.Recent, entry =>
                entry.RunId == "cli-run-4" && entry.ModelSource == ReviewModelSource.Explicit);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
