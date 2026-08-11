using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewPromptBuilderTests
{
    [Theory]
    [InlineData("code")]
    [InlineData("security")]
    [InlineData("performance")]
    public void Build_UsesVersionedKindTemplateAndInsertionPoints(string kind)
    {
        var prompt = new ReviewPromptBuilder().Build("src/Thing.cs", kind, "No globals.", "Project rule.", "class Thing { }");

        Assert.Contains($"File {kind} review v1", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`src/Thing.cs`", prompt, StringComparison.Ordinal);
        Assert.Contains("No globals.", prompt, StringComparison.Ordinal);
        Assert.Contains("Project rule.", prompt, StringComparison.Ordinal);
        Assert.Contains("class Thing { }", prompt, StringComparison.Ordinal);
        Assert.Contains("Strict output format", prompt, StringComparison.Ordinal);
        Assert.Contains("ruleId", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UsesExplicitDefaultsForGuidelineInsertionPoints()
    {
        var prompt = new ReviewPromptBuilder().Build("Thing.cs", "code");

        Assert.Equal(2, prompt.Split("(none supplied)", StringSplitOptions.None).Length - 1);
        Assert.Contains("(content not supplied)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsUnsupportedKind()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ReviewPromptBuilder().Build("Thing.cs", "accessibility"));

        Assert.Contains("Unsupported review kind", exception.Message, StringComparison.Ordinal);
    }
}

public sealed class ReviewResponseParserTests
{
    [Fact]
    public void Parse_AcceptsSingleFencedContract()
    {
        var parsed = new ReviewResponseParser().Parse("before```json\n" + ValidResponse + "\n```after");

        Assert.Equal("Looks sound.", parsed["summary"]!.GetValue<string>());
        Assert.Empty(parsed["findings"]!.AsArray());
    }

    [Fact]
    public void Parse_RejectsFindingWithoutFileLocation()
    {
        var response = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [{\"id\":\"bad-id\",\"ruleId\":\"correctness.bad\",\"aspect\":\"correctness\",\"severity\":\"high\",\"title\":\"Bad\",\"description\":\"Bad.\",\"recommendation\":\"Fix.\",\"locations\":[]}]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
        Assert.Contains("location", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AcceptsFindingWithStrictLocationContract()
    {
        var response = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding + "]",
            StringComparison.Ordinal);

        var finding = new ReviewResponseParser().Parse(response)["findings"]!.AsArray()[0]!.AsObject();

        Assert.Equal("medium", finding["severity"]!.GetValue<string>());
        Assert.Equal(1, finding["locations"]!.AsArray()[0]!["range"]!["start"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public void Parse_RejectsFindingWithoutRuleId()
    {
        var response = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"ruleId\":\"correctness.risk\",", string.Empty, StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
        Assert.Contains("ruleId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAgentClaimingDeterministicSource()
    {
        var response = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace(
                "\"locations\":",
                "\"source\":{\"kind\":\"deterministic\",\"sensorId\":\"fake\",\"producer\":\"fake\"},\"locations\":",
                StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<ReviewResponseException>(() =>
            new ReviewResponseParser().Parse(response));

        Assert.Contains("cannot claim deterministic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAgentClaimingVerifiedReproductionOrTypedEvidence()
    {
        var verified = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"locations\":", "\"reproduction\":{\"status\":\"verified\"},\"locations\":", StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);
        var typed = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"locations\":", "\"evidenceItems\":[],\"locations\":", StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);

        Assert.Contains("cannot claim verified", Assert.Throws<ReviewResponseException>(
            () => new ReviewResponseParser().Parse(verified)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot claim runner", Assert.Throws<ReviewResponseException>(
            () => new ReviewResponseParser().Parse(typed)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("```json\n{}\n```\n```json\n{}\n```")]
    public void Parse_RejectsMissingOrAmbiguousJson(string response)
    {
        Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
    }

    [Theory]
    [InlineData("101", "A")]
    [InlineData("95", "B")]
    public void Parse_RejectsInvalidGrade(string score, string band)
    {
        var response = ValidResponse
            .Replace("95, \"band\": \"A\"", $"{score}, \"band\": \"{band}\"", StringComparison.Ordinal);

        Assert.Throws<ReviewResponseException>(() => new ReviewResponseParser().Parse(response));
    }

    [Fact]
    public void Parse_RejectsUnknownAspectAndSeverity()
    {
        var unknownAspect = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"correctness\"", "\"security\"", StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);
        var invalidSeverity = ValidResponse.Replace(
            "\"findings\": []",
            "\"findings\": [" + ValidFinding.Replace("\"medium\"", "\"urgent\"", StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);

        Assert.Contains("unknown aspect", Assert.Throws<ReviewResponseException>(
            () => new ReviewResponseParser().Parse(unknownAspect)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("severity", Assert.Throws<ReviewResponseException>(
            () => new ReviewResponseParser().Parse(invalidSeverity)).Message, StringComparison.OrdinalIgnoreCase);
    }

    internal const string ValidResponse = """
        {
          "grade": { "score": 95, "band": "A", "rationale": "Correct and clear." },
          "summary": "Looks sound.",
          "aspects": [
            { "id": "correctness", "title": "Correctness", "grade": { "score": 95, "band": "A", "rationale": "No issue found." } }
          ],
          "findings": []
        }
        """;

    internal const string ValidFinding = """
        {"id":"correctness-1","ruleId":"correctness.risk","aspect":"correctness","severity":"medium","title":"Risk","description":"A risk.","recommendation":"Fix it.","locations":[{"path":"src/Small.cs","range":{"start":{"line":1,"column":1},"end":{"line":1,"column":8}}}]}
        """;
}

public sealed class ReviewRunnerTests
{
    [Fact]
    public async Task ReviewAsync_WritesFreshQs3Metadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "Small.cs");
        await File.WriteAllTextAsync(file, "internal static class Small { }\n", cancellationToken);
        try
        {
            var result = await new ReviewRunner(new FakeAgent()).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), cancellationToken);

            Assert.True(File.Exists(result.MetaPath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var json = document.RootElement;
            Assert.Equal("code", json.GetProperty("kind").GetString());
            Assert.Equal("file", json.GetProperty("unit").GetProperty("level").GetString());
            Assert.Equal("src/Small.cs", json.GetProperty("unit").GetProperty("path").GetString());
            Assert.Equal(result.ReviewedHash, json.GetProperty("reviewedHash").GetProperty("value").GetString());
            Assert.StartsWith(Path.Combine(root, "src", ".quality", "reviews", "files"), result.MetaPath, StringComparison.Ordinal);
            Assert.NotNull(result.Observation);
            Assert.StartsWith("src/.quality/reviews/files/file.", result.Observation.SidecarPath, StringComparison.Ordinal);
            Assert.EndsWith(".review-meta.code.json", result.Observation.SidecarPath, StringComparison.Ordinal);
            Assert.DoesNotContain(root, result.Observation.ReviewMetaJson, StringComparison.Ordinal);
            Assert.Equal("sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(result.Observation.ReviewMetaJson))).ToLowerInvariant(),
                result.Observation.SidecarSha256);

            // Independently verify the stored manifest against the current file bytes.
            var currentContentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(file, cancellationToken);
            var currentManifest = ReviewSubjectHasher.ComputeManifestHash(
                json.GetProperty("unit").GetProperty("id").GetString()!,
                [new SubjectInputHash("src/Small.cs", "file", currentContentHash)]);
            Assert.Equal(result.ReviewedHash, currentManifest);
            var directUsage = Assert.Single((await UsageLedger.QueryAsync(
                root, cancellationToken: cancellationToken)).Recent);
            Assert.Equal(UsageLedger.CurrentSchemaVersion, directUsage.SchemaVersion);
            Assert.Null(directUsage.ReviewRunId);
            Assert.Equal("unknown", directUsage.SourceRevision);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReviewAsync_PropagatesAgentAndReviewInputsIntoMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithReviewFileAsync(async (root, file) =>
        {
            var inputDirectory = Path.Combine(root, ".quality", "inputs");
            Directory.CreateDirectory(inputDirectory);
            await File.WriteAllTextAsync(Path.Combine(inputDirectory, "security.md"),
                "---\nid: secure-boundaries\nkinds: [security]\nlevels: [file]\npriority: 50\n---\nTreat external data as untrusted.\n", cancellationToken);
            var agent = new FakeAgent(response: ReviewResponseParserTests.ValidResponse.Replace(
                "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal),
                model: "effective-model", cliType: "codex", thinkingLevel: "high");

            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                "src/Small.cs", "security", GlobalGuidelines: "Global rule.", ProjectGuidelines: "Project rule.", RepositoryRoot: root,
                ReviewRunId: "review-parent", RequestedModel: "requested-alias", RequestedCliType: "codex",
                RequestedThinkingLevel: "high"),
                cancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var json = document.RootElement;
            var schema = RepositoryTestContext.Schema("review-meta.v3.schema.json");
            var validation = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
            Assert.Equal("security", json.GetProperty("kind").GetString());
            Assert.Equal("test-agent", json.GetProperty("reviewer").GetProperty("agent").GetString());
            Assert.Equal("effective-model", json.GetProperty("reviewer").GetProperty("model").GetString());
            Assert.Equal("high", json.GetProperty("reviewer").GetProperty("thinkingLevel").GetString());
            Assert.Equal("run-test", json.GetProperty("reviewer").GetProperty("runId").GetString());
            var usage = json.GetProperty("reviewer").GetProperty("usage");
            Assert.Equal("codex", usage.GetProperty("cliType").GetString());
            Assert.Equal(120, usage.GetProperty("inputTokens").GetInt64());
            Assert.Equal(34, usage.GetProperty("outputTokens").GetInt64());
            Assert.Equal(56, usage.GetProperty("cachedInputTokens").GetInt64());
            Assert.Equal(890, usage.GetProperty("durationMs").GetInt64());
            var ledger = await UsageLedger.QueryAsync(root, kind: "security", cancellationToken: cancellationToken);
            var ledgerEntry = Assert.Single(ledger.Recent);
            Assert.Equal("run-test", ledgerEntry.RunId);
            Assert.Equal("src/Small.cs", ledgerEntry.Path);
            Assert.Equal(120, ledger.InputTokens);
            Assert.StartsWith("finding-", json.GetProperty("findings")[0].GetProperty("id").GetString(), StringComparison.Ordinal);
            Assert.Equal("correctness.risk", json.GetProperty("findings")[0].GetProperty("ruleId").GetString());
            Assert.StartsWith("sha256:", json.GetProperty("findings")[0].GetProperty("fingerprint").GetString(), StringComparison.Ordinal);
            var captured = json.GetProperty("findings")[0].GetProperty("locations")[0].GetProperty("capturedExcerpt");
            Assert.Equal("internal", captured.GetProperty("text").GetString());
            Assert.Matches("^sha256:[a-f0-9]{64}$", captured.GetProperty("contentHash").GetString());
            var sourceEvidence = Assert.Single(json.GetProperty("findings")[0].GetProperty("evidenceItems").EnumerateArray());
            Assert.Equal("source-span", sourceEvidence.GetProperty("class").GetString());
            Assert.Equal("observed", sourceEvidence.GetProperty("status").GetString());
            Assert.Equal("unknown", json.GetProperty("findings")[0].GetProperty("reproduction").GetProperty("status").GetString());
            var origin = json.GetProperty("origin");
            Assert.Equal("requested-alias", origin.GetProperty("requested").GetProperty("model").GetString());
            Assert.Equal("effective-model", origin.GetProperty("executed").GetProperty("model").GetString());
            Assert.Equal("high", origin.GetProperty("executed").GetProperty("thinkingLevel").GetString());
            Assert.Equal("review-parent", origin.GetProperty("reviewRunId").GetString());
            Assert.Contains("Global rule.", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("Project rule.", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("Treat external data as untrusted.", agent.Prompt, StringComparison.Ordinal);
            var standard = Assert.Single(json.GetProperty("reviewInputs").GetProperty("standards").EnumerateArray());
            Assert.Equal("secure-boundaries", standard.GetProperty("id").GetString());
            Assert.Equal("project", standard.GetProperty("scope").GetString());
            Assert.Equal(root, agent.WorkingDirectory);
        });
    }

    [Fact]
    public async Task SecurityReview_MergesPlantedSecretIntoOneBlockingStatement()
    {
        await WithReviewFileAsync(async (root, file) =>
        {
            await File.WriteAllTextAsync(file, "const string Token = \"planted-test-secret\";\n",
                TestContext.Current.CancellationToken);
            var sensor = FakeSensor.BlockingSecret();
            var registry = new SensorRegistry([sensor]);
            var agentResponse = ReviewResponseParserTests.ValidResponse.Replace(
                "\"findings\": []",
                "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]",
                StringComparison.Ordinal);
            var agent = new FakeAgent(agentResponse);

            var result = await new ReviewRunner(agent, sensorRegistry: registry).ReviewAsync(new ReviewRequest(
                "src/Small.cs",
                "security",
                RepositoryRoot: root,
                Sensors: [new ReviewSensorConfiguration(sensor.Id)]),
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.MetaPath, TestContext.Current.CancellationToken));
            var metadata = document.RootElement;
            Assert.Equal("block", metadata.GetProperty("security").GetProperty("verdict").GetString());
            Assert.Equal(59, metadata.GetProperty("grade").GetProperty("score").GetInt32());
            var finding = Assert.Single(metadata.GetProperty("findings").EnumerateArray());
            Assert.Equal("gitleaks-planted-secret", finding.GetProperty("id").GetString());
            Assert.Equal("secrets", finding.GetProperty("aspect").GetString());
            Assert.Contains("\"source\": \"machine-sensor\"", finding.GetProperty("evidence").GetString(), StringComparison.Ordinal);
            var sensorReference = Assert.Single(metadata.GetProperty("reviewer").GetProperty("sensors").EnumerateArray());
            Assert.Equal("gitleaks", sensorReference.GetProperty("id").GetString());
            Assert.Matches("^sha256:[a-f0-9]{64}$", sensorReference.GetProperty("resultHash").GetString());
            Assert.Contains("\"id\": \"gitleaks\"", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("machine-produced sensor evidence", agent.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Single(Directory.EnumerateFiles(root, "*.review-meta.security.json", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public async Task DeterministicEvidence_RemainsSeparateAndDoesNotMoveAgentGrade()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var finding = new ReviewFinding(
                "roslyn-ca1822-aaaaaaaaaaaa",
                "analyzer",
                FindingSeverity.Medium,
                "Mark members as static",
                "Member does not access instance data.",
                "Make the member static.",
                [new FindingLocation("src/Small.cs", new FindingRange(
                    new FindingPosition(1, 1), new FindingPosition(1, 5)))],
                "sha256:" + new string('a', 64),
                "CA1822",
                Source: new FindingSource(
                    FindingSourceKind.Deterministic,
                    "roslyn",
                    "Microsoft.CodeAnalysis",
                    "4.14.0",
                    0));
            var evidence = new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(
                    "roslyn",
                    "1.0.0",
                    "repository",
                    ".",
                    "2026-07-26T10:00:00.000Z",
                    new Dictionary<string, string> { ["Microsoft.CodeAnalysis"] = "4.14.0" }));
            var agent = new FakeAgent();

            var result = await new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest(
                    "src/Small.cs",
                    RepositoryRoot: root,
                    DeterministicEvidence: [evidence]),
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal(95, document.RootElement.GetProperty("grade").GetProperty("score").GetInt32());
            Assert.Empty(document.RootElement.GetProperty("findings").EnumerateArray());
            var stored = Assert.Single(document.RootElement.GetProperty("deterministicEvidence").EnumerateArray());
            var storedFinding = Assert.Single(stored.GetProperty("findings").EnumerateArray());
            Assert.Equal("CA1822", storedFinding.GetProperty("ruleId").GetString());
            Assert.Equal("deterministic",
                storedFinding.GetProperty("source").GetProperty("kind").GetString());
            Assert.Contains("Judge their applicability", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("deduplicate", agent.Prompt, StringComparison.Ordinal);
            Assert.Contains("does not set or cap", agent.Prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SecurityReview_UnavailableSensorCannotBecomeClean()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var sensor = FakeSensor.Unavailable();
            var result = await new ReviewRunner(new FakeAgent(), sensorRegistry: new SensorRegistry([sensor]))
                .ReviewAsync(new ReviewRequest(
                    "src/Small.cs",
                    "security",
                    RepositoryRoot: root,
                    Sensors: [new ReviewSensorConfiguration(sensor.Id)]),
                    TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.MetaPath, TestContext.Current.CancellationToken));
            var metadata = document.RootElement;
            Assert.Equal("unavailable", metadata.GetProperty("security").GetProperty("verdict").GetString());
            Assert.Equal("F", metadata.GetProperty("grade").GetProperty("band").GetString());
            Assert.Equal(59, metadata.GetProperty("grade").GetProperty("score").GetInt32());
            Assert.Contains("not a clean result", metadata.GetProperty("summary").GetString(), StringComparison.Ordinal);
            var storedSensor = Assert.Single(metadata.GetProperty("security").GetProperty("sensors").EnumerateArray());
            Assert.False(storedSensor.GetProperty("available").GetBoolean());
            Assert.Equal("test sensor is offline", storedSensor.GetProperty("unavailableReason").GetString());
            Assert.Empty(metadata.GetProperty("findings").EnumerateArray());
        });
    }

    [Fact]
    public async Task ProjectSecurityReview_UsesNamedPostureAspects()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var sensor = FakeSensor.Pass();
            var result = await new ReviewRunner(new FakeAgent(), sensorRegistry: new SensorRegistry([sensor]))
                .ReviewAsync(new ReviewRequest(
                    ".",
                    "security",
                    ReviewLevel.Project,
                    RepositoryRoot: root,
                    UnitId: "qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    SubjectFiles: ["src/Small.cs"],
                    Sensors: [new ReviewSensorConfiguration(sensor.Id)]),
                    TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                result.MetaPath, TestContext.Current.CancellationToken));
            var aspects = document.RootElement.GetProperty("aspects").EnumerateArray()
                .Select(aspect => aspect.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("secrets", aspects);
            Assert.Contains("dependencies", aspects);
            Assert.Contains("authentication-authorization", aspects);
            Assert.Contains("input-validation", aspects);
            Assert.Contains("configuration-iac", aspects);
        });
    }

    [Fact]
    public async Task ReviewAsync_WritesAggregateMetadataForNonFileLevel()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var agent = new FakeAgent();
            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                ".", Level: ReviewLevel.Project, RepositoryRoot: root,
                UnitId: "qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", SubjectFiles: ["src/Small.cs"], DisplayName: "Test project",
                AggregateExclusions: [new ScopeExclusion("src/Generated.g.cs", "Generated source")]),
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal("project", document.RootElement.GetProperty("unit").GetProperty("level").GetString());
            Assert.Equal("qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", document.RootElement.GetProperty("unit").GetProperty("id").GetString());
            Assert.Equal("Test project", document.RootElement.GetProperty("unit").GetProperty("displayName").GetString());
            Assert.Equal("aggregate-members", Assert.Single(document.RootElement.GetProperty("subjectInputs").EnumerateArray()).GetProperty("selector").GetString());
            var excluded = Assert.Single(document.RootElement.GetProperty("aggregate").GetProperty("excluded").EnumerateArray());
            Assert.Equal("src/Generated.g.cs", excluded.GetProperty("path").GetString());
            Assert.Equal("Generated source", excluded.GetProperty("reason").GetString());
            Assert.Single(document.RootElement.GetProperty("aggregate").GetProperty("members").EnumerateArray());
            Assert.Contains("src/Small.cs", agent.Prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ReviewIfNeededAsync_skips_unchanged_files_and_aggregates_and_invalidates_affected_units()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var firstPath = Path.Combine(root, "src", "First.cs");
        var secondPath = Path.Combine(root, "src", "Second.cs");
        await File.WriteAllTextAsync(firstPath, "internal static class First { }\n", cancellationToken);
        await File.WriteAllTextAsync(secondPath, "internal static class Second { }\n", cancellationToken);
        try
        {
            var agent = new FakeAgent();
            var recordedUsage = new List<ReviewUsageEntry>();
            var runner = new ReviewRunner(agent, usageRecorded: recordedUsage.Add);
            var first = new ReviewRequest("src/First.cs", RepositoryRoot: root);
            var second = new ReviewRequest("src/Second.cs", RepositoryRoot: root);
            var aggregate = new ReviewRequest(
                ".",
                Level: ReviewLevel.Project,
                RepositoryRoot: root,
                UnitId: "qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                SubjectFiles: ["src/First.cs", "src/Second.cs"],
                DisplayName: "Test project");

            await runner.ReviewAsync(first, cancellationToken);
            await runner.ReviewAsync(second, cancellationToken);
            await runner.ReviewAsync(aggregate, cancellationToken);

            var freshFirst = await runner.ReviewIfNeededAsync(first, cancellationToken: cancellationToken);
            var freshSecond = await runner.ReviewIfNeededAsync(second, cancellationToken: cancellationToken);
            var freshAggregate = await runner.ReviewIfNeededAsync(aggregate, cancellationToken: cancellationToken);
            Assert.True(freshFirst.SkippedFresh);
            Assert.True(freshSecond.SkippedFresh);
            Assert.True(freshAggregate.SkippedFresh);
            Assert.NotNull(freshFirst.Observation);
            Assert.NotNull(freshSecond.Observation);
            Assert.NotNull(freshAggregate.Observation);
            Assert.Null(freshFirst.Review);
            Assert.Equal(3, agent.RunCount);
            Assert.Equal(3, recordedUsage.Count);

            await File.AppendAllTextAsync(firstPath, "// changed\n", cancellationToken);

            Assert.False((await runner.ReviewIfNeededAsync(first, cancellationToken: cancellationToken)).SkippedFresh);
            Assert.True((await runner.ReviewIfNeededAsync(second, cancellationToken: cancellationToken)).SkippedFresh);
            Assert.False((await runner.ReviewIfNeededAsync(aggregate, cancellationToken: cancellationToken)).SkippedFresh);
            Assert.Equal(5, agent.RunCount);
            Assert.Equal(5, recordedUsage.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ScopeChangeMakesStoredAggregateStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Demo"));
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        await File.WriteAllTextAsync(Path.Combine(root, "Demo.slnx"),
            "<Solution><Project Path=\"src/Demo/Demo.csproj\" /></Solution>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "Keep.cs"),
            "namespace Demo; internal sealed class Keep { }", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Demo", "Fixture.cs"),
            "namespace Demo; internal sealed class Fixture { }", TestContext.Current.CancellationToken);
        var scopePath = Path.Combine(root, ".quality", "scope.json");
        await File.WriteAllTextAsync(scopePath,
            "{\"rules\":[{\"action\":\"exclude\",\"pattern\":\"**/Fixture.cs\",\"reason\":\"Test fixture\"}]}",
            TestContext.Current.CancellationToken);
        try
        {
            var original = Assert.Single(RepositoryHierarchyBuilder.BuildDotNet(root));
            var originalFiles = original.Children.SelectMany(module => module.Children)
                .SelectMany(ns => ns.Children).Where(node => node.Level == ReviewLevel.File).ToArray();
            await new ReviewRunner(new FakeAgent()).ReviewAsync(new ReviewRequest(
                original.Path, Level: ReviewLevel.Project, RepositoryRoot: root, UnitId: original.Id,
                SubjectFiles: originalFiles.Select(file => file.Path).ToArray(),
                SubjectUnits: originalFiles.Select(file => new ReviewSubjectFile(file.Id, file.Path)).ToArray(),
                AggregateExclusions: original.Exclusions), TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(scopePath,
                "{\"rules\":[{\"action\":\"include\",\"pattern\":\"**/Fixture.cs\"}]}",
                TestContext.Current.CancellationToken);
            var changed = Assert.Single(RepositoryHierarchyBuilder.BuildDotNet(root));
            ReviewMetaDiscovery.AttachDiscovered(root, [changed]);

            Assert.Equal(ReviewState.Stale, changed.Documents[ReviewKind.Code].State);
            Assert.Empty(changed.Exclusions);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReviewIfNeededAsync_rereviews_when_review_inputs_change()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var agent = new FakeAgent();
            var runner = new ReviewRunner(agent);
            var request = new ReviewRequest("src/Small.cs", RepositoryRoot: root);
            await runner.ReviewAsync(request, TestContext.Current.CancellationToken);
            var inputDirectory = Path.Combine(root, ".quality", "inputs");
            Directory.CreateDirectory(inputDirectory);
            await File.WriteAllTextAsync(Path.Combine(inputDirectory, "code.md"),
                "---\nid: current-rule\nkinds: [code]\nlevels: [file]\npriority: 50\n---\nApply the current rule.\n",
                TestContext.Current.CancellationToken);

            var execution = await runner.ReviewIfNeededAsync(
                request, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(execution.SkippedFresh);
            Assert.Equal(2, agent.RunCount);
        });
    }

    [Fact]
    public async Task ReviewIfNeededAsync_rereviews_when_requested_model_changes()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var request = new ReviewRequest("src/Small.cs", RepositoryRoot: root);
            var firstAgent = new FakeAgent(model: "model-a");
            await new ReviewRunner(firstAgent).ReviewAsync(request, TestContext.Current.CancellationToken);

            var secondAgent = new FakeAgent(model: "model-b");
            var secondRunner = new ReviewRunner(secondAgent);
            var changed = await secondRunner.ReviewIfNeededAsync(
                request, cancellationToken: TestContext.Current.CancellationToken);
            var unchanged = await secondRunner.ReviewIfNeededAsync(
                request, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(changed.SkippedFresh);
            Assert.True(unchanged.SkippedFresh);
            Assert.Equal(1, secondAgent.RunCount);
        });
    }

    [Fact]
    public async Task ReviewIfNeededAsync_force_bypasses_freshness_gate()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var agent = new FakeAgent();
            var runner = new ReviewRunner(agent);
            var request = new ReviewRequest("src/Small.cs", RepositoryRoot: root);
            await runner.ReviewAsync(request, TestContext.Current.CancellationToken);

            var execution = await runner.ReviewIfNeededAsync(
                request, force: true, TestContext.Current.CancellationToken);

            Assert.False(execution.SkippedFresh);
            Assert.Equal(2, agent.RunCount);
        });
    }

    [Fact]
    public async Task ReviewAsync_RejectsTargetOutsideRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => new ReviewRunner(new FakeAgent()).ReviewAsync(
                new ReviewRequest(outside, RepositoryRoot: root), TestContext.Current.CancellationToken));
            Assert.Contains("inside", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReviewAsync_RejectsDirectTargetExcludedByRepositoryScope()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "src/Small.cs\n",
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => new ReviewRunner(new FakeAgent()).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken));

            Assert.Contains("is excluded", exception.Message, StringComparison.Ordinal);
            Assert.Contains(".gitignore:1", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ReviewAsync_DoesNotWriteMetadataWhenTargetChangesDuringReview()
    {
        await WithReviewFileAsync(async (root, file) =>
        {
            var agent = new FakeAgent(onRun: () => File.AppendAllText(file, "// changed\n"));

            var exception = await Assert.ThrowsAsync<ReviewRunException>(() => new ReviewRunner(agent).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken));

            Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.json", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public async Task ReviewAsync_PersistsAndReportsUsageWhenResponseValidationFails()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var recorded = new List<ReviewUsageEntry>();
            var runner = new ReviewRunner(new FakeAgent(response: "{}"), usageRecorded: recorded.Add);

            await Assert.ThrowsAsync<ReviewResponseException>(() => runner.ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root, ReviewRunId: "review-sweep-test"),
                TestContext.Current.CancellationToken));

            Assert.Equal("run-test", Assert.Single(recorded).RunId);
            Assert.Equal("review-sweep-test", recorded[0].ReviewRunId);
            Assert.Equal(UsageLedger.CurrentSchemaVersion, recorded[0].SchemaVersion);
            Assert.Equal("unknown", recorded[0].SourceRevision);
            Assert.Equal(120, recorded[0].Tokens.InputTokens);
            var report = await UsageLedger.QueryAsync(root, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("run-test", Assert.Single(report.Recent).RunId);
            Assert.Equal("review-sweep-test", Assert.Single(report.ByReviewRun).Key);
        });
    }

    [Fact]
    public async Task ReviewAsync_AppendsAgentReplyAndPreservesThreadHistory()
    {
        await WithReviewFileAsync(async (root, file) =>
        {
            var initial = await new ReviewRunner(new FakeAgent()).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            var range = new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 1));
            var meta = JsonNode.Parse(await File.ReadAllTextAsync(initial.MetaPath, TestContext.Current.CancellationToken))!.AsObject();
            meta["threads"] = new JsonArray(new JsonObject
            {
                ["id"] = "thread-1",
                ["anchor"] = new JsonObject
                {
                    ["path"] = "src/Small.cs", ["fingerprint"] = "sha256:" + new string('a', 64),
                    ["contextHash"] = ReviewThreadManager.ComputeContextHash(content, range),
                    ["lastKnownRange"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 1, ["column"] = 1 }, ["end"] = new JsonObject { ["line"] = 1, ["column"] = 1 } },
                },
                ["status"] = "open", ["entries"] = new JsonArray(new JsonObject
                {
                    ["id"] = "entry-human", ["author"] = new JsonObject { ["kind"] = "human", ["name"] = "Ada" },
                    ["createdAt"] = "2026-07-21T10:00:00.000Z", ["body"] = "Is this intentional?",
                }),
            });
            await File.WriteAllTextAsync(initial.MetaPath, meta.ToJsonString(), TestContext.Current.CancellationToken);
            var response = ReviewResponseParserTests.ValidResponse.TrimEnd().TrimEnd('}') +
                ",\n\"threadUpdates\":[{\"threadId\":\"thread-1\",\"body\":\"Yes; the type is deliberately internal.\",\"replyTo\":\"entry-human\",\"status\":\"resolved\"}]}";
            var agent = new FakeAgent(response);

            await new ReviewRunner(agent).ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken);

            Assert.Contains("Is this intentional?", agent.Prompt, StringComparison.Ordinal);
            using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(initial.MetaPath, TestContext.Current.CancellationToken));
            var thread = Assert.Single(stored.RootElement.GetProperty("threads").EnumerateArray());
            Assert.Equal("resolved", thread.GetProperty("status").GetString());
            var entries = thread.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal("Ada", entries[0].GetProperty("author").GetProperty("name").GetString());
            Assert.Equal("test-agent", entries[1].GetProperty("author").GetProperty("agent").GetString());
            Assert.Equal("deterministic", entries[1].GetProperty("author").GetProperty("model").GetString());
        });
    }

    [Fact]
    public void LoadAndHeal_MovesToNearestMatchingContextAndDetachesMissingContext()
    {
        var root = Directory.CreateTempSubdirectory("quality-thread-heal-");
        try
        {
            var range = new FindingRange(new FindingPosition(2, 1), new FindingPosition(2, 1));
            var contextHash = ReviewThreadManager.ComputeContextHash("before\ntarget\nafter", range);
            var metaPath = Path.Combine(root.FullName, "meta.json");
            static JsonObject Thread(string id, string fingerprint, string hash, int line) => new()
            {
                ["id"] = id,
                ["anchor"] = new JsonObject
                {
                    ["path"] = "a.cs", ["fingerprint"] = fingerprint, ["contextHash"] = hash,
                    ["lastKnownRange"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = line, ["column"] = 1 },
                        ["end"] = new JsonObject { ["line"] = line, ["column"] = 1 },
                    },
                },
                ["status"] = "open", ["entries"] = new JsonArray(),
            };
            var stored = new JsonObject { ["threads"] = new JsonArray(
                Thread("moving", "sha256:" + new string('a', 64), contextHash, 2),
                Thread("gone", "sha256:" + new string('b', 64), "sha256:" + new string('c', 64), 1)) };
            File.WriteAllText(metaPath, stored.ToJsonString());

            var threads = ReviewThreadManager.LoadAndHeal(metaPath, "a.cs", "added\nbefore\ntarget\nafter");

            Assert.Equal("healed", threads[0]!["anchorState"]!.GetValue<string>());
            Assert.Equal(3, threads[0]!["anchor"]!["lastKnownRange"]!["start"]!["line"]!.GetValue<int>());
            Assert.Equal("detached", threads[1]!["anchorState"]!.GetValue<string>());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task ReviewAsync_PersistsUsageWhenAgentExecutionFails()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var recorded = new List<ReviewUsageEntry>();
            var runner = new ReviewRunner(new FailingAgent(), usageRecorded: recorded.Add);

            await Assert.ThrowsAsync<ReviewAgentRunException>(() => runner.ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken));

            Assert.Equal(321, Assert.Single(recorded).Tokens.InputTokens);
            Assert.Equal("failed-run", Assert.Single((await UsageLedger.QueryAsync(root,
                cancellationToken: TestContext.Current.CancellationToken)).Recent).RunId);
        });
    }

    private static async Task WithReviewFileAsync(Func<string, string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "Small.cs");
        await File.WriteAllTextAsync(file, "internal static class Small { }\n", TestContext.Current.CancellationToken);
        try
        {
            await test(root, file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeAgent : IReviewAgent
    {
        private readonly string _response;
        private readonly Action? _onRun;
        private readonly string _model;

        public FakeAgent(string? response = null, Action? onRun = null, string model = "deterministic",
            string cliType = "test-agent", string? thinkingLevel = null)
        {
            _response = response ?? ReviewResponseParserTests.ValidResponse;
            _onRun = onRun;
            _model = model;
            CliType = cliType;
            ThinkingLevel = thinkingLevel;
        }

        public string AgentName => "test-agent";

        public string? Model => _model;

        public string CliType { get; }

        public string? ThinkingLevel { get; }

        public string? Prompt { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public int RunCount { get; private set; }

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            RunCount++;
            Prompt = prompt;
            WorkingDirectory = workingDirectory;
            _onRun?.Invoke();
            return Task.FromResult(new ReviewAgentResult("run-test", $"```json\n{_response}\n```",
                new TokenUsage(120, 34, 56, 7, 890), _model));
        }
    }

    private sealed class FailingAgent : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => "requested-model";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromException<ReviewAgentResult>(new ReviewAgentRunException("failed-run",
                new TokenUsage(321, 12, 30, 2, 456), "effective-model", new IOException("stream failed")));
    }

    private sealed class FakeSensor(
        bool available,
        string? unavailableReason,
        IReadOnlyList<ReviewFinding> findings) : IReviewSensor
    {
        public string Id => "gitleaks";
        public string Version => "8.24.2";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(available, unavailableReason,
                new Dictionary<string, string> { ["gitleaks"] = Version }));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorScanResult(
                available,
                unavailableReason,
                findings,
                new SensorProvenance(
                    Id,
                    Version,
                    "repository",
                    ".",
                    "2026-07-25T10:00:00.000Z",
                    new Dictionary<string, string> { ["gitleaks"] = Version })));

        public static FakeSensor BlockingSecret() => new(
            true,
            null,
            [new ReviewFinding(
                "gitleaks-planted-secret",
                "secrets",
                FindingSeverity.High,
                "Planted test secret",
                "Gitleaks detected a high-confidence test secret.",
                "Remove and rotate the credential.",
                [new FindingLocation(
                    "src/Small.cs",
                    new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 8)))],
                "sha256:" + new string('b', 64),
                "generic-api-key")]);

        public static FakeSensor Unavailable() => new(false, "test sensor is offline", []);

        public static FakeSensor Pass() => new(true, null, []);
    }
}

public sealed class LiveReviewIntegrationTests
{
    [Fact]
    public async Task CodexCanReviewSmallFile_WhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QUALITY_RUN_LIVE_REVIEW"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set QUALITY_RUN_LIVE_REVIEW=1 to run the installed Codex CLI integration.");
        }

        var root = RepositoryTestContext.FindRepositoryRoot();
        var result = await new ReviewRunner().ReviewAsync(new ReviewRequest(
            "src/AgentOrchestrator.CodeQuality/StalenessState.cs",
            RepositoryRoot: root), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result.MetaPath));
    }

}
