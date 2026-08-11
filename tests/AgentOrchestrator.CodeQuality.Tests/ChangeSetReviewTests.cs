using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class ChangeSetReviewTests
{
    [Fact]
    public void Committed_twenty_transition_sample_validates_against_the_contract()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(root, "schemas", "change-review.v1.schema.json")));
        var samples = Directory.GetFiles(Path.Combine(root, ".quality", "changes"), "*.json");

        Assert.Equal(20, samples.Length);
        foreach (var path in samples)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var evaluation = schema.Evaluate(json.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
            });
            Assert.True(evaluation.IsValid, $"{Path.GetFileName(path)}: {evaluation}");
        }
    }

    [Fact]
    public async Task Merge_range_detects_lower_grade_new_finding_boundary_and_staleness()
    {
        using var repository = await TestRepository.CreateAsync();
        const string original = "var app = WebApplication.Create();\n";
        await repository.WriteAsync("src/Api.cs", original);
        await repository.WriteMetaAsync(score: 92, findings: []);
        var @base = await repository.CommitAsync("base");
        await repository.WriteAsync("src/Api.cs",
            "var app = WebApplication.Create();\napp.MapGet(\"/orders\", () => \"ok\");\n");
        await repository.WriteMetaAsync(score: 78,
        [
            new TestFinding("sha256:" + new string('a', 64), "api-auth", "high", "Endpoint lacks authorization"),
        ], original);
        var head = await repository.CommitAsync("add endpoint");

        var provider = new GitMergeRangeChangeSetProvider();
        var result = Assert.Single(await new ChangeSetReviewService().ReviewAsync(
            provider,
            new ChangeSetQuery(repository.Root, @base, head),
            new ChangeReviewOptions(Persist: false),
            TestContext.Current.CancellationToken));

        Assert.Equal(ChangeReviewVerdict.Regression, result.Document.Verdict);
        var grade = Assert.Single(result.Document.Delta.Grades);
        Assert.Equal(-14, grade.ScoreChange);
        Assert.True(grade.Regression);
        Assert.Equal("src/Api.cs", grade.UnitPath);
        Assert.Equal("api-auth", Assert.Single(result.Document.Delta.Findings.New).RuleId);
        var boundary = Assert.Single(result.Document.Delta.Boundaries.New);
        Assert.Equal("GET /orders", boundary.Name);
        Assert.Equal("src/Api.cs", boundary.Path);
        var stale = Assert.Single(result.Document.Delta.NewlyStale);
        Assert.Equal("src/Api.cs", stale.UnitPath);
        Assert.Contains("changed", Assert.Single(stale.Reasons), StringComparison.Ordinal);
        Assert.True(result.Document.Economy.DiffCharacters > 0);
        Assert.True(result.Document.Economy.FullSweepCharacters > 0);
        Assert.Contains(result.Document.TouchedUnits, unit => unit.Level == "project");
        Assert.Contains(result.Document.TouchedUnits, unit => unit.Level == "module");
        Assert.Contains(result.Document.TouchedUnits, unit => unit.Id == TestRepository.UnitId);
    }

    [Fact]
    public async Task Pure_rename_reports_no_quality_delta()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/Api.cs", "app.MapGet(\"/health\", () => \"ok\");\n");
        var @base = await repository.CommitAsync("base");
        await repository.MoveAsync("src/Api.cs", "src/MovedApi.cs");
        var head = await repository.CommitAsync("move only");

        var result = Assert.Single(await new ChangeSetReviewService().ReviewAsync(
            new GitMergeRangeChangeSetProvider(),
            new ChangeSetQuery(repository.Root, @base, head),
            new ChangeReviewOptions(Persist: false),
            TestContext.Current.CancellationToken));

        Assert.True(result.Document.Delta.OnlyMoves);
        Assert.False(result.Document.Delta.HasQualityDelta);
        Assert.Equal(ChangeReviewVerdict.NoQualityDelta, result.Document.Verdict);
        Assert.Empty(result.Document.Delta.Boundaries.New);
        Assert.Equal("No quality delta: the change set only moved files without changing their content.",
            result.Document.Summary);
    }

    [Fact]
    public async Task Two_parent_merge_keeps_base_topic_head_and_merge_identity_distinct()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("README.md", "base\n");
        var @base = await repository.CommitAsync("base");
        await repository.GitCommandAsync("checkout", "-q", "-b", "topic");
        await repository.WriteAsync("topic.txt", "topic\n");
        var topic = await repository.CommitAsync("topic");
        await repository.GitCommandAsync("checkout", "-q", "-");
        await repository.GitCommandAsync("merge", "--no-ff", "-q", "-m", "merge topic", "topic");
        var merge = (await repository.GitCommandAsync("rev-parse", "HEAD")).Trim();

        var change = Assert.Single(await new GitMergeRangeChangeSetProvider().GetAsync(
            new ChangeSetQuery(repository.Root, @base, merge),
            TestContext.Current.CancellationToken));

        Assert.Equal(@base, change.BaseCommit);
        Assert.Equal(topic, change.HeadCommit);
        Assert.Equal(merge, change.MergeCommit);
        Assert.Equal(merge, change.ResultCommit);
        Assert.Equal(ChangeSetReviewService.GetPath(repository.Root, change),
            Path.Combine(repository.Root, ".quality", "changes", merge + ".json"));
    }

    [Fact]
    public async Task Provider_contract_accepts_a_second_trivial_provider_and_injected_reviewer()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("README.md", "before\n");
        var @base = await repository.CommitAsync("base");
        await repository.WriteAsync("README.md", "after\n");
        var head = await repository.CommitAsync("head");
        var gitChange = Assert.Single(await new GitMergeRangeChangeSetProvider().GetAsync(
            new ChangeSetQuery(repository.Root, @base, head),
            TestContext.Current.CancellationToken));
        var provider = new TrivialProvider(gitChange with { Provider = "test-provider" });
        var reviewAgent = new TrivialReviewAgent();

        var result = Assert.Single(await new ChangeSetReviewService().ReviewAsync(
            provider,
            new ChangeSetQuery(repository.Root),
            new ChangeReviewOptions(Persist: false, Reviewer: new AgentChangeDeltaReviewer(reviewAgent)),
            TestContext.Current.CancellationToken));

        Assert.True(provider.WasCalled);
        Assert.Equal("test-provider", result.Document.ChangeSet.Provider);
        Assert.Equal("complete", result.Document.Judgement.Status);
        Assert.Equal("test-model", result.Document.Judgement.Reviewer);
        Assert.Contains("diff --git", reviewAgent.Prompt, StringComparison.Ordinal);
        Assert.Contains("Deterministic evidence:", reviewAgent.Prompt, StringComparison.Ordinal);
        Assert.Equal(
            ["risk", "test-evidence", "scope-discipline", "architecture-drift"],
            result.Document.Judgement.Aspects.Select(aspect => aspect.Id));
    }

    [Fact]
    public async Task Fail_on_regression_uses_documented_exit_codes()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.WriteAsync("src/Api.cs", "var app = WebApplication.Create();\n");
        await repository.WriteMetaAsync(95, []);
        var @base = await repository.CommitAsync("base");
        await repository.WriteAsync("src/Api.cs",
            "var app = WebApplication.Create();\napp.MapPost(\"/admin\", () => \"ok\");\n");
        await repository.WriteMetaAsync(70, []);
        var head = await repository.CommitAsync("regression");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await ChangeDiffCommand.RunAsync(
            [repository.Root, "--base", @base, "--head", head, "--fail-on-regression", "--no-write"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(ChangeDiffCommand.RegressionExitCode, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("grade      src/Api.cs", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("boundary   new POST /admin", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(ChangeDiffCommand.ErrorExitCode, await ChangeDiffCommand.RunAsync(
            ["--base"], new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken));
    }

    private sealed class TrivialProvider(ChangeSet changeSet) : IChangeSetProvider
    {
        public bool WasCalled { get; private set; }
        public string Id => "test-provider";

        public Task<IReadOnlyList<ChangeSet>> GetAsync(
            ChangeSetQuery query,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<ChangeSet>>([changeSet]);
        }
    }

    private sealed class TrivialReviewAgent : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => "test-model";
        public string Prompt { get; private set; } = string.Empty;

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            return Task.FromResult(new ReviewAgentResult(
                "test-run",
                """
                {
                  "summary": "Small change.",
                  "aspects": [
                    { "id": "risk", "title": "Risk of the change", "verdict": "good", "rationale": "Small diff." },
                    { "id": "test-evidence", "title": "Test evidence", "verdict": "unknown", "rationale": "No test signal." },
                    { "id": "scope-discipline", "title": "Scope discipline", "verdict": "good", "rationale": "One claimed file." },
                    { "id": "architecture-drift", "title": "Architecture drift", "verdict": "good", "rationale": "No drift." }
                  ]
                }
                """,
                EffectiveModel: "test-model"));
        }
    }

    private sealed record TestFinding(string Fingerprint, string RuleId, string Severity, string Title);

    private sealed class TestRepository : IDisposable
    {
        public const string UnitId = "qs-v1/test/file/api";
        private const string MetaPath = "src/.quality/reviews/files/api.review-meta.code.json";

        private TestRepository(string root) => Root = root;
        public string Root { get; }

        public static async Task<TestRepository> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "quality-change-tests", Guid.NewGuid().ToString("N"));
            var repository = new TestRepository(root);
            await GitTestRepository.InitializeAsync(root, TestContext.Current.CancellationToken);
            return repository;
        }

        public async Task WriteAsync(string relativePath, string content)
        {
            var path = Absolute(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        }

        public async Task WriteMetaAsync(
            int score,
            IReadOnlyList<TestFinding> findings,
            string? reviewedContent = null)
        {
            var source = reviewedContent ??
                         await File.ReadAllTextAsync(Absolute("src/Api.cs"), TestContext.Current.CancellationToken);
            var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))));
            var meta = new
            {
                schemaVersion = 2,
                unit = new { id = UnitId, adapter = "generic", level = "file", path = "src/Api.cs", displayName = "Api.cs" },
                kind = "code",
                grade = new
                {
                    score,
                    band = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F",
                    rationale = "Test grade",
                },
                subjectInputs = new[] { new { path = "src/Api.cs", selector = "file", contentHash } },
                findings = findings.Select(finding => new
                {
                    id = "finding-" + finding.Fingerprint[7..],
                    fingerprint = finding.Fingerprint,
                    ruleId = finding.RuleId,
                    severity = finding.Severity,
                    title = finding.Title,
                }),
            };
            await WriteAsync(MetaPath, JsonSerializer.Serialize(meta));
        }

        public async Task<string> CommitAsync(string message)
        {
            await GitAsync("add", "-A");
            await GitAsync("commit", "--quiet", "-m", message);
            return (await GitAsync("rev-parse", "HEAD")).Trim();
        }

        public async Task MoveAsync(string from, string to) => await GitAsync("mv", from, to);

        private string Absolute(string path) =>
            Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

        public Task<string> GitCommandAsync(params string[] arguments) => GitAsync(arguments);

        private async Task<string> GitAsync(params string[] arguments)
            => await GitTestRepository.RunForOutputAsync(Root, TestContext.Current.CancellationToken, arguments);

        public void Dispose()
        {
            TestDirectory.Delete(Root);
        }
    }
}
