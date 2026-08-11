using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewEvidenceV3Tests
{
    [Fact]
    public async Task Runner_captures_typed_evidence_and_requested_and_executed_route()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-studio-evidence-v3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Subject.cs");
        await File.WriteAllTextAsync(path, "public class Subject { }\n", TestContext.Current.CancellationToken);
        Assert.NotNull(CoverageSensor.GitValue(root, "init", "--quiet"));
        Assert.NotNull(CoverageSensor.GitValue(root, "add", "Subject.cs"));
        Assert.NotNull(CoverageSensor.GitValue(root, "-c", "user.name=Quality Studio", "-c",
            "user.email=quality@example.invalid", "commit", "--quiet", "-m", "initial subject"));
        await File.AppendAllTextAsync(path, "// uncommitted review input\n", TestContext.Current.CancellationToken);
        try
        {
            var result = await new ReviewRunner(new EvidenceAgent()).ReviewAsync(new ReviewRequest(
                path, RepositoryRoot: root, ReviewRunId: "review-parent",
                RequestedRoute: new RequestedReviewRoute("raw-request-model", "medium", "codex")),
                TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(result.MetaPath, TestContext.Current.CancellationToken);
            using var generated = JsonDocument.Parse(json);
            var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas", "review-meta.v3.schema.json"),
                TestContext.Current.CancellationToken));
            var validation = schema.Evaluate(generated.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
            var document = ReviewMetaV3Json.Deserialize(json);
            var finding = Assert.Single(document.Findings);

            Assert.Equal(3, document.SchemaVersion);
            Assert.Equal("raw-request-model", finding.Origin.Requested.Model);
            Assert.Equal("medium", finding.Origin.Requested.ThinkingLevel);
            Assert.Equal("executed-model", finding.Origin.Executed.Model);
            Assert.Equal("high", finding.Origin.Executed.ThinkingLevel);
            Assert.Equal("review-parent", finding.Origin.ReviewRunId);
            Assert.EndsWith("+uncommitted", finding.Origin.SourceRevision, StringComparison.Ordinal);
            Assert.Equal("public class Subject", Assert.Single(finding.Anchors).CapturedExcerpt.Text);
            Assert.StartsWith("sha256:", finding.Anchors[0].CapturedExcerpt.ContentHash, StringComparison.Ordinal);
            Assert.Contains(finding.Evidence, evidence => evidence.Class == "source-span" && evidence.Status == "observed");
            Assert.Contains(finding.Evidence, evidence => evidence.Class == "external-reference" && evidence.Status == "claimed");
            Assert.Equal("specified", finding.Reproduction.Status);

            var usage = await UsageLedger.QueryAsync(root, cancellationToken: TestContext.Current.CancellationToken);
            var entry = Assert.Single(usage.Recent);
            Assert.Equal(3, entry.SchemaVersion);
            Assert.Equal("raw-request-model", entry.RequestedModel);
            Assert.Equal("medium", entry.RequestedThinkingLevel);
            Assert.Equal("high", entry.ThinkingLevel);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("verified", null)]
    [InlineData("specified", "deterministic-result")]
    public void Agent_cannot_claim_verified_or_deterministic_evidence(string reproductionStatus, string? evidenceClass)
    {
        var response = JsonNode.Parse(ReviewResponseParserTests.ValidResponse.Replace(
            "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal))!.AsObject();
        var finding = response["findings"]!.AsArray()[0]!.AsObject();
        finding["reproduction"] = new JsonObject
        {
            ["status"] = reproductionStatus,
            ["steps"] = new JsonArray("Run the check"),
            ["attempts"] = new JsonArray(),
        };
        if (evidenceClass is not null)
        {
            finding["evidence"] = new JsonArray(new JsonObject
            {
                ["id"] = "claimed",
                ["class"] = evidenceClass,
                ["status"] = "claimed",
                ["summary"] = "Claimed result",
                ["reference"] = "ref",
            });
        }

        Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response.ToJsonString()));
    }

    [Fact]
    public void Agent_cannot_smuggle_untyped_provenance_into_a_v3_finding()
    {
        var response = JsonNode.Parse(ReviewResponseParserTests.ValidResponse.Replace(
            "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal))!.AsObject();
        response["findings"]![0]!["verifiedBy"] = "agent-assertion";

        var error = Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response.ToJsonString()));
        Assert.Contains("Unexpected finding property", error.Message, StringComparison.Ordinal);
    }

    private sealed class EvidenceAgent : IReviewAgent
    {
        public string AgentName => "codex";
        public string CliType => "codex";
        public string? Model => "requested-model";
        public string? ThinkingLevel => "high";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("operation-1", """
                {
                  "grade": { "score": 80, "band": "B", "rationale": "One actionable issue." },
                  "summary": "One issue.",
                  "aspects": [{ "id": "correctness", "title": "Correctness", "grade": { "score": 80, "band": "B", "rationale": "One issue." } }],
                  "findings": [{
                    "id": "agent-id",
                    "ruleId": "built-in:code",
                    "aspect": "correctness",
                    "severity": "high",
                    "title": "Public type lacks a namespace",
                    "description": "The public type is declared in the global namespace.",
                    "impact": "Consumers can encounter ambiguous type names.",
                    "recommendation": "Place the type in a named namespace.",
                    "locations": [{ "path": "Subject.cs", "range": { "start": { "line": 1, "column": 1 }, "end": { "line": 1, "column": 20 } } }],
                    "evidence": [{ "id": "language-guidance", "class": "external-reference", "status": "claimed", "summary": "Namespacing is recommended.", "reference": "dotnet-design-guidelines" }],
                    "reproduction": { "status": "specified", "steps": ["Compile this file and inspect the fully qualified type name."], "expected": "The type has a namespace.", "observed": "The source declares none.", "attempts": [] }
                  }],
                  "threadUpdates": []
                }
                """, new TokenUsage(100, 20, 0, 5, 250), "executed-model", "high"));
    }
}
