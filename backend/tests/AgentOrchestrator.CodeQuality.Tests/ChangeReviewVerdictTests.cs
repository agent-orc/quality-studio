using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class ChangeReviewVerdictTests
{
    [Fact]
    public async Task Changed_line_is_cited_stable_and_policy_controls_blocking()
    {
        using var repository = await Fixture.CreateAsync();
        var agent = new FakeAgent("""{"findings":[{"ruleId":"QS-CS-003","path":"src/Work.cs","line":2,"message":"Missing cancellation handling","evidenceChecked":"The added await has no token","missing":"CancellationToken forwarding"}]}""");
        var service = new ChangeReviewVerdictService(agent);
        var request = new ChangeReviewVerdictRequest(repository.Base, repository.Head);
        var first = await service.ReviewAsync("sample", repository.Root, request,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await service.ReviewAsync("sample", repository.Root, request,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("block", first.Verdict);
        var finding = Assert.Single(first.Findings);
        Assert.Equal("QS-CS-003", finding.RuleId);
        Assert.Equal("block", finding.Disposition);
        Assert.Equal(first.PolicyHash, second.PolicyHash);
        Assert.Equal(first.RuleSetHash, second.RuleSetHash);
        Assert.Equal(finding.Fingerprint, Assert.Single(second.Findings).Fingerprint);
        Assert.Equal(finding.Id, Assert.Single(second.Findings).Id);
        var schemaRoot = RepositoryTestContext.FindRepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaRoot, "schemas", "change-review-verdict.v1.schema.json")));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(schema.Evaluate(json.RootElement).IsValid);

        await File.WriteAllTextAsync(Path.Combine(repository.Root, ".quality", "policy.json"),
            """{"schemaVersion":1,"blockingRules":["QS-CS-005"],"blockingSeverities":["high"]}""",
            TestContext.Current.CancellationToken);
        var changedPolicy = await service.ReviewAsync("sample", repository.Root, request,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("concerns", changedPolicy.Verdict);
        Assert.NotEqual(first.PolicyHash, changedPolicy.PolicyHash);
    }

    [Fact]
    public async Task Out_of_diff_or_unparseable_reply_never_passes()
    {
        using var repository = await Fixture.CreateAsync();
        var outOfDiff = new FakeAgent("""{"findings":[{"ruleId":"QS-CS-003","path":"src/Work.cs","line":1,"message":"Old code","evidenceChecked":"Old line","missing":"Token"}]}""");
        var result = await new ChangeReviewVerdictService(outOfDiff).ReviewAsync(
            "sample", repository.Root, new(repository.Base, repository.Head),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("unparseable", result.Verdict);
        Assert.Empty(result.Findings);

        var malformed = await new ChangeReviewVerdictService(new FakeAgent("not JSON")).ReviewAsync(
            "sample", repository.Root, new(repository.Base, repository.Head),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("unparseable", malformed.Verdict);
    }

    [Fact]
    public async Task Fenced_reviewer_json_is_accepted()
    {
        using var repository = await Fixture.CreateAsync();
        var agent = new FakeAgent("```json\n{\"findings\":[]}\n```");
        var result = await new ChangeReviewVerdictService(agent).ReviewAsync(
            "sample", repository.Root, new(repository.Base, repository.Head),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("pass", result.Verdict);
    }

    [Fact]
    public async Task Removed_line_can_cite_base_side()
    {
        using var repository = await Fixture.CreateAsync(deletionOnly: true);
        var agent = new FakeAgent("""{"findings":[{"ruleId":"QS-CS-003","path":"src/Work.cs","side":"base","line":2,"message":"Removed cancellation forwarding","evidenceChecked":"The removed call forwarded the token","missing":"The remaining call has no token"}]}""");
        var result = await new ChangeReviewVerdictService(agent).ReviewAsync(
            "sample", repository.Root, new(repository.Base, repository.Head),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("block", result.Verdict);
        Assert.Equal("base", Assert.Single(result.Findings).Side);
    }

    private sealed class FakeAgent(string response) : IReviewAgent
    {
        public string AgentName => "fixture";
        public string? Model => "fixture";
        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult("fixture-run", response));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string @base, string head) => (Root, Base, Head) = (root, @base, head);
        public string Root { get; }
        public string Base { get; }
        public string Head { get; }

        public static async Task<Fixture> CreateAsync(bool deletionOnly = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "qs-verdict-tests", Guid.NewGuid().ToString("N"));
            await GitTestRepository.InitializeAsync(root, TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Work.cs"),
                deletionOnly ? "class Work {}\nawait operation.RunAsync(token);\n" : "class Work {}\n",
                TestContext.Current.CancellationToken);
            await GitTestRepository.RunAsync(root, TestContext.Current.CancellationToken, "add", "-A");
            await GitTestRepository.RunAsync(root, TestContext.Current.CancellationToken, "commit", "-qm", "base");
            var @base = (await GitTestRepository.RunForOutputAsync(root, TestContext.Current.CancellationToken, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Work.cs"),
                deletionOnly ? "class Work {}\n" : "class Work {}\nawait operation.RunAsync();\n",
                TestContext.Current.CancellationToken);
            await GitTestRepository.RunAsync(root, TestContext.Current.CancellationToken, "add", "-A");
            await GitTestRepository.RunAsync(root, TestContext.Current.CancellationToken, "commit", "-qm", "head");
            var head = (await GitTestRepository.RunForOutputAsync(root, TestContext.Current.CancellationToken, "rev-parse", "HEAD")).Trim();
            return new Fixture(root, @base, head);
        }

        public void Dispose() => TemporaryDirectory.Delete(Root);
    }
}
