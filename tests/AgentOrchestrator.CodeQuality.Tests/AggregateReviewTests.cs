using System.Text.Json;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewTemplateSelectionTests
{
    [Theory]
    [InlineData(ReviewLevel.Module, "code", "module-code-review")]
    [InlineData(ReviewLevel.Module, "security", "module-security-review")]
    [InlineData(ReviewLevel.Project, "code", "project-code-review")]
    [InlineData(ReviewLevel.Project, "security", "project-security-review")]
    [InlineData(ReviewLevel.File, "code", "file-code-review")]
    public void TemplateId_NamesTheLevelTemplateWhenOneExists(ReviewLevel level, string kind, string expected) =>
        Assert.Equal(expected, ReviewPromptBuilder.TemplateId(level, kind));

    [Theory]
    [InlineData(ReviewLevel.Project, "performance")]
    [InlineData(ReviewLevel.Module, "performance")]
    [InlineData(ReviewLevel.Namespace, "code")]
    [InlineData(ReviewLevel.Function, "security")]
    public void TemplateId_FallsBackToTheFileTemplateWithoutALevelTemplate(ReviewLevel level, string kind)
    {
        Assert.Equal($"file-{kind}-review", ReviewPromptBuilder.TemplateId(level, kind));
        Assert.Equal(ReviewPromptBuilder.TemplateHash(kind), ReviewPromptBuilder.TemplateHash(level, kind));
    }

    [Fact]
    public void TemplateHash_SeparatesLevelsThatHaveTheirOwnTemplate()
    {
        Assert.NotEqual(ReviewPromptBuilder.TemplateHash(ReviewLevel.File, "code"),
            ReviewPromptBuilder.TemplateHash(ReviewLevel.Module, "code"));
        Assert.NotEqual(ReviewPromptBuilder.TemplateHash(ReviewLevel.Module, "code"),
            ReviewPromptBuilder.TemplateHash(ReviewLevel.Project, "code"));
        Assert.Equal(ReviewPromptBuilder.TemplateHash("code"),
            ReviewPromptBuilder.TemplateHash(ReviewLevel.File, "code"));
    }

    [Fact]
    public void Build_RendersTheLevelTemplateWithoutLeftoverPlaceholders()
    {
        var prompt = new ReviewPromptBuilder().Build("src/Demo/Demo.csproj", "code",
            fileContent: "digest", level: ReviewLevel.Module);

        Assert.Contains("# Module code review v1", prompt, StringComparison.Ordinal);
        Assert.Contains("architecture", prompt, StringComparison.Ordinal);
        Assert.Contains("duplication", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }
}

public sealed class AggregateReviewTests
{
    [Fact]
    public async Task ModuleReview_SendsADigestOfItsMembersInsteadOfConcatenatedSource()
    {
        await WithModuleAsync(async root =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var fileRunner = new ReviewRunner(new RecordingAgent(CleanResponse));
            await fileRunner.ReviewAsync(new ReviewRequest("src/Demo/A.cs", RepositoryRoot: root), cancellationToken);
            await fileRunner.ReviewAsync(new ReviewRequest("src/Demo/B.cs", RepositoryRoot: root), cancellationToken);

            var agent = new RecordingAgent(ModuleResponse);
            var result = await new ReviewRunner(agent).ReviewAsync(ModuleRequest(root), cancellationToken);
            var prompt = agent.Prompt!;

            Assert.Contains("# Module code review v1", prompt, StringComparison.Ordinal);
            Assert.Contains("# module review subject digest: Demo", prompt, StringComparison.Ordinal);
            Assert.Contains("## Members and their file-level review state", prompt, StringComparison.Ordinal);
            // The member sidecars this run has just written are what the aggregate pass reads.
            Assert.Contains("| src/Demo/A.cs |", prompt, StringComparison.Ordinal);
            Assert.Contains("| src/Demo/B.cs |", prompt, StringComparison.Ordinal);
            Assert.Equal(2, CountOccurrences(prompt, "| A/95 | none |"));
            Assert.Contains("Members with a current code review: 2 of 2", prompt, StringComparison.Ordinal);
            // The derived structure the planner passed in, not a second hierarchy derivation.
            Assert.Contains("- namespace Demo (src/Demo) - 2 direct file(s)", prompt, StringComparison.Ordinal);
            // Real source with real line numbers, never the old concatenation.
            Assert.Contains("### src/Demo/A.cs (", prompt, StringComparison.Ordinal);
            Assert.Contains("     5 | ", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("--- src/Demo/A.cs ---", prompt, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var reviewInputs = document.RootElement.GetProperty("reviewInputs").GetProperty("prompt");
            Assert.Equal("module-code-review", reviewInputs.GetProperty("id").GetString());
            Assert.Equal(ReviewPromptBuilder.TemplateHash(ReviewLevel.Module, "code"),
                reviewInputs.GetProperty("contentHash").GetString());
            Assert.Equal("aggregate-members",
                document.RootElement.GetProperty("subjectInputs")[0].GetProperty("selector").GetString());
        });
    }

    [Fact]
    public async Task ProjectSecurityReview_CarriesTheDerivedBoundaryInventory()
    {
        await WithModuleAsync(async root =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(QualityDataRoot.PathFor(root, ".quality/boundaries"));
            await File.WriteAllTextAsync(
                QualityDataRoot.PathFor(root, ".quality/boundaries/inventory.json"), BoundaryInventoryJson, cancellationToken);

            var agent = new RecordingAgent(CleanResponse);
            await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                ".",
                "security",
                ReviewLevel.Project,
                RepositoryRoot: root,
                UnitId: ProjectUnitId,
                SubjectFiles: ["src/Demo/A.cs", "src/Demo/B.cs"],
                DisplayName: "Demo"), cancellationToken);
            var prompt = agent.Prompt!;

            Assert.Contains("# Project security review v1", prompt, StringComparison.Ordinal);
            Assert.Contains("## Derived boundary inventory", prompt, StringComparison.Ordinal);
            Assert.Contains("derived 1 entry point(s) in scope", prompt, StringComparison.Ordinal);
            Assert.Contains("src/Demo/A.cs:5 | http-route/inbound | GET /api/items", prompt, StringComparison.Ordinal);
            Assert.Contains("authorization=unknown", prompt, StringComparison.Ordinal);
            Assert.Contains("sideEffects=process", prompt, StringComparison.Ordinal);
            Assert.Contains("`secrets`, `dependencies`, `authentication-authorization`", prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ProjectSecurityReview_SaysSoWhenNoBoundaryInventoryWasScanned()
    {
        await WithModuleAsync(async root =>
        {
            var agent = new RecordingAgent(CleanResponse);
            await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                ".",
                "security",
                ReviewLevel.Project,
                RepositoryRoot: root,
                UnitId: ProjectUnitId,
                SubjectFiles: ["src/Demo/A.cs"],
                DisplayName: "Demo"), TestContext.Current.CancellationToken);

            Assert.Contains("No boundary inventory has been scanned", agent.Prompt!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ModuleReview_PersistsARollupFindingWithEveryLocationAndCheckedCitations()
    {
        await WithModuleAsync(async root =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var memberResult = await new ReviewRunner(new RecordingAgent(MemberFindingResponse)).ReviewAsync(
                new ReviewRequest("src/Demo/A.cs", RepositoryRoot: root), cancellationToken);
            using var memberDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(memberResult.MetaPath, cancellationToken));
            var memberFingerprint = memberDocument.RootElement.GetProperty("findings")[0]
                .GetProperty("fingerprint").GetString()!;

            var result = await new ReviewRunner(new RecordingAgent(RollupResponse(memberFingerprint)))
                .ReviewAsync(ModuleRequest(root), cancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var finding = Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray());
            Assert.False(finding.TryGetProperty("relatedFindings", out _));
            Assert.Equal(["src/Demo/A.cs", "src/Demo/B.cs"], finding.GetProperty("locations").EnumerateArray()
                .Select(location => location.GetProperty("path").GetString()!).ToArray());

            var anchors = finding.GetProperty("anchors").EnumerateArray().ToArray();
            Assert.Equal(2, anchors.Length);
            Assert.Equal("primary", anchors[0].GetProperty("role").GetString());
            Assert.Equal("src/Demo/A.cs", anchors[0].GetProperty("path").GetString());
            Assert.Equal("related", anchors[1].GetProperty("role").GetString());
            Assert.Equal("related-2", anchors[1].GetProperty("id").GetString());
            Assert.Equal("src/Demo/B.cs", anchors[1].GetProperty("path").GetString());
            Assert.Contains("Contains", anchors[1].GetProperty("capturedExcerpt").GetProperty("text").GetString(),
                StringComparison.Ordinal);
            Assert.Matches("^sha256:[a-f0-9]{64}$",
                anchors[1].GetProperty("capturedExcerpt").GetProperty("excerptHash").GetString());

            var evidence = finding.GetProperty("evidenceItems").EnumerateArray()
                .ToDictionary(item => item.GetProperty("id").GetString()!);
            Assert.Equal("primary", evidence["ev-source"].GetProperty("anchorId").GetString());
            Assert.Equal("sourceSpan", evidence["ev-source-2"].GetProperty("class").GetString());
            Assert.Equal("observed", evidence["ev-source-2"].GetProperty("status").GetString());
            Assert.Equal("related-2", evidence["ev-source-2"].GetProperty("anchorId").GetString());
            Assert.Equal("legacyClaim", evidence["ev-member-1"].GetProperty("class").GetString());
            Assert.Equal("observed", evidence["ev-member-1"].GetProperty("status").GetString());
            Assert.Contains(memberFingerprint, evidence["ev-member-1"].GetProperty("summary").GetString()!,
                StringComparison.Ordinal);
            Assert.Equal("unverified", evidence["ev-member-2"].GetProperty("status").GetString());

            var states = await new FindingStateStore(root).ReadAsync(cancellationToken);
            Assert.Contains(finding.GetProperty("fingerprint").GetString()!, states.Keys);
        });
    }

    [Fact]
    public async Task FileReview_KeepsItsSingleAnchorAndDropsAnyCitationArray()
    {
        await WithModuleAsync(async root =>
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var result = await new ReviewRunner(new RecordingAgent(
                    MemberFindingResponse.Replace("\"recommendation\": \"Compare ordinally.\"",
                        "\"recommendation\": \"Compare ordinally.\", \"relatedFindings\": [\"sha256:" + new string('b', 64) + "\"]",
                        StringComparison.Ordinal)))
                .ReviewAsync(new ReviewRequest("src/Demo/A.cs", RepositoryRoot: root), cancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var finding = Assert.Single(document.RootElement.GetProperty("findings").EnumerateArray());
            Assert.False(finding.TryGetProperty("relatedFindings", out _));
            Assert.Single(finding.GetProperty("anchors").EnumerateArray());
            Assert.Single(finding.GetProperty("evidenceItems").EnumerateArray());
        });
    }

    [Fact]
    public async Task Digest_KeepsTheLargestMemberProportionalWithinItsBudget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "Small.cs"),
            "namespace Demo;\n\ninternal sealed class Small { }\n", cancellationToken);
        var monolith = string.Join('\n', Enumerable.Range(1, 4_000)
            .Select(index => index % 20 == 0
                ? $"    public static int Member{index}() => {index};"
                : $"        var filler{index} = {index};"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "Monolith.cs"), monolith, cancellationToken);
        try
        {
            var digest = await AggregateSubjectDigest.BuildAsync(new AggregateDigestRequest(
                root, "src/Demo", "Demo", ReviewLevel.Module, "code",
                ["src/Demo/Monolith.cs", "src/Demo/Small.cs"], [], [], BudgetCharacters: 12_000), cancellationToken);

            Assert.True(digest.Text.Length <= 12_000 + 2_000, $"digest was {digest.Text.Length} characters");
            // The small member survives beside the monolith, and the monolith arrives as signatures.
            Assert.DoesNotContain("\r", digest.Text, StringComparison.Ordinal);
            Assert.Contains("### src/Demo/Small.cs (", digest.Text, StringComparison.Ordinal);
            Assert.Contains("     3 | internal sealed class Small { }", digest.Text, StringComparison.Ordinal);
            Assert.Contains("declaration outline", digest.Text, StringComparison.Ordinal);
            Assert.Contains("public static int Member20()", digest.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("var filler21 = 21;", digest.Text, StringComparison.Ordinal);
            Assert.Empty(digest.MemberFindings);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    private static ReviewRequest ModuleRequest(string root) => new(
        "src/Demo/Demo.csproj",
        "code",
        ReviewLevel.Module,
        RepositoryRoot: root,
        UnitId: ModuleUnitId,
        SubjectFiles: ["src/Demo/A.cs", "src/Demo/B.cs"],
        DisplayName: "Demo",
        SubjectGroups: [new ReviewSubjectGroup(ReviewLevel.Namespace, "Demo", "src/Demo",
            ["src/Demo/A.cs", "src/Demo/B.cs"])]);

    private const string ModuleUnitId =
        "qs-v1/dotnet/module/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string ProjectUnitId =
        "qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static async Task WithModuleAsync(Func<string, Task> test)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "A.cs"), Member("A"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "B.cs"), Member("B"), cancellationToken);
        try
        {
            await test(root);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "quality-aggregate-tests", Guid.NewGuid().ToString("N"));

    // Both members carry the same defect class, which is what an aggregate review is for.
    private static string Member(string name) => $$"""
        namespace Demo;

        internal static class {{name}}
        {
            public static bool Has(string value) => value.ToLower().Contains("token");
        }

        """;

    private const string CleanResponse = """
        {
          "grade": { "score": 95, "band": "A", "rationale": "Cohesive." },
          "summary": "Sound.",
          "aspects": [
            { "id": "correctness", "title": "Correctness", "grade": { "score": 95, "band": "A", "rationale": "Clear." } }
          ],
          "findings": []
        }
        """;

    private const string ModuleResponse = """
        {
          "grade": { "score": 82, "band": "B", "rationale": "Cohesive, with one repeated rule." },
          "summary": "Two members implement the same containment rule.",
          "aspects": [
            { "id": "architecture", "title": "Architecture", "grade": { "score": 85, "band": "B", "rationale": "One responsibility." } },
            { "id": "duplication", "title": "Duplication", "grade": { "score": 70, "band": "C", "rationale": "One repeated rule." } }
          ],
          "findings": []
        }
        """;

    private const string MemberFindingResponse = """
        {
          "grade": { "score": 72, "band": "C", "rationale": "One containment defect." },
          "summary": "Culture-sensitive containment.",
          "aspects": [
            { "id": "correctness", "title": "Correctness", "grade": { "score": 72, "band": "C", "rationale": "One defect." } }
          ],
          "findings": [
            {
              "id": "member-1", "ruleId": "built-in:code", "aspect": "correctness", "severity": "medium",
              "title": "Culture-sensitive containment", "description": "ToLower plus Contains.",
              "recommendation": "Compare ordinally.",
              "locations": [{ "path": "src/Demo/A.cs", "range": { "start": { "line": 5, "column": 5 }, "end": { "line": 5, "column": 76 } } }]
            }
          ]
        }
        """;

    private static string RollupResponse(string memberFingerprint) => $$"""
        {
          "grade": { "score": 68, "band": "D", "rationale": "The same rule is written twice." },
          "summary": "One containment rule, two implementations.",
          "aspects": [
            { "id": "duplication", "title": "Duplication", "grade": { "score": 68, "band": "D", "rationale": "Two copies." } }
          ],
          "findings": [
            {
              "id": "rollup-1", "ruleId": "built-in:code", "aspect": "duplication", "severity": "high",
              "title": "Culture-sensitive containment in two members",
              "description": "A.cs and B.cs both lower-case before Contains.",
              "recommendation": "Extract one ordinal comparison.",
              "relatedFindings": ["{{memberFingerprint}}", "sha256:{{new string('c', 64)}}"],
              "locations": [
                { "path": "src/Demo/A.cs", "range": { "start": { "line": 5, "column": 5 }, "end": { "line": 5, "column": 76 } } },
                { "path": "src/Demo/B.cs", "range": { "start": { "line": 5, "column": 5 }, "end": { "line": 5, "column": 76 } } }
              ]
            }
          ]
        }
        """;

    private const string BoundaryInventoryJson = """
        {
          "$schema": "https://agent-orchestrator.dev/quality/schemas/boundary-inventory.v1.schema.json",
          "schemaVersion": 1,
          "sensor": "boundaries",
          "sensorVersion": "1.0.0",
          "entries": [
            {
              "id": "route-1",
              "kind": "http-route",
              "direction": "inbound",
              "name": "GET /api/items",
              "transport": "http",
              "location": { "path": "src/Demo/A.cs", "line": 5 },
              "reachability": { "value": "remote", "derivedFrom": ["MapGet"] },
              "authentication": { "value": "unknown", "derivedFrom": [] },
              "authorization": { "value": "unknown", "derivedFrom": [] },
              "inputs": [{ "name": "id", "source": "query", "type": "string", "required": true }],
              "response": { "shape": "json", "contentType": "application/json" },
              "sideEffects": ["process"],
              "rateLimit": { "value": "unknown", "derivedFrom": [] },
              "sizeLimit": { "value": "unknown", "derivedFrom": [] },
              "knownConsumers": [],
              "evidence": ["app.MapGet"]
            }
          ],
          "findings": []
        }
        """;

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class RecordingAgent(string response) : IReviewAgent
    {
        public string AgentName => "test-agent";

        public string? Model => "deterministic";

        public string? Prompt { get; private set; }

        public Task<ReviewAgentResult> RunAsync(
            string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            return Task.FromResult(new ReviewAgentResult(
                "run-aggregate", $"```json\n{response}\n```", new TokenUsage(10, 5, 0, 0, 1), Model));
        }
    }
}
