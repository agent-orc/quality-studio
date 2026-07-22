using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
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

            // Independently verify the stored manifest against the current file bytes.
            var currentContentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(file, cancellationToken);
            var currentManifest = ReviewSubjectHasher.ComputeManifestHash(
                json.GetProperty("unit").GetProperty("id").GetString()!,
                [new SubjectInputHash("src/Small.cs", "file", currentContentHash)]);
            Assert.Equal(result.ReviewedHash, currentManifest);
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
                "\"findings\": []", "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]", StringComparison.Ordinal));

            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                "src/Small.cs", "security", GlobalGuidelines: "Global rule.", ProjectGuidelines: "Project rule.", RepositoryRoot: root),
                cancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, cancellationToken));
            var json = document.RootElement;
            Assert.Equal("security", json.GetProperty("kind").GetString());
            Assert.Equal("test-agent", json.GetProperty("reviewer").GetProperty("agent").GetString());
            Assert.Equal("deterministic", json.GetProperty("reviewer").GetProperty("model").GetString());
            Assert.Equal("run-test", json.GetProperty("reviewer").GetProperty("runId").GetString());
            var usage = json.GetProperty("reviewer").GetProperty("usage");
            Assert.Equal("test-agent", usage.GetProperty("cliType").GetString());
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
    public async Task ReviewAsync_WritesAggregateMetadataForNonFileLevel()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var agent = new FakeAgent();
            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                ".", Level: ReviewLevel.Project, RepositoryRoot: root,
                UnitId: "qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", SubjectFiles: ["src/Small.cs"], DisplayName: "Test project"),
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal("project", document.RootElement.GetProperty("unit").GetProperty("level").GetString());
            Assert.Equal("qs-v1/dotnet/project/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", document.RootElement.GetProperty("unit").GetProperty("id").GetString());
            Assert.Equal("Test project", document.RootElement.GetProperty("unit").GetProperty("displayName").GetString());
            Assert.Equal("aggregate-members", Assert.Single(document.RootElement.GetProperty("subjectInputs").EnumerateArray()).GetProperty("selector").GetString());
            Assert.Empty(document.RootElement.GetProperty("aggregate").GetProperty("excluded").EnumerateArray());
            Assert.Single(document.RootElement.GetProperty("aggregate").GetProperty("members").EnumerateArray());
            Assert.Contains("src/Small.cs", agent.Prompt, StringComparison.Ordinal);
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
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken));

            Assert.Equal("run-test", Assert.Single(recorded).RunId);
            Assert.Equal(120, recorded[0].Tokens.InputTokens);
            Assert.Equal("run-test", Assert.Single((await UsageLedger.QueryAsync(root,
                cancellationToken: TestContext.Current.CancellationToken)).Recent).RunId);
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

        public FakeAgent(string? response = null, Action? onRun = null)
        {
            _response = response ?? ReviewResponseParserTests.ValidResponse;
            _onRun = onRun;
        }

        public string AgentName => "test-agent";

        public string? Model => "deterministic";

        public string? Prompt { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            WorkingDirectory = workingDirectory;
            _onRun?.Invoke();
            return Task.FromResult(new ReviewAgentResult("run-test", $"```json\n{_response}\n```",
                new TokenUsage(120, 34, 56, 7, 890), "deterministic"));
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

        var root = FindRepositoryRoot();
        var result = await new ReviewRunner().ReviewAsync(new ReviewRequest(
            "src/AgentOrchestrator.CodeQuality/StalenessState.cs",
            RepositoryRoot: root), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(result.MetaPath));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
