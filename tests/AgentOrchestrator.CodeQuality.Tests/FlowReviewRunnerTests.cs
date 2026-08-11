using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class FlowReviewRunnerTests
{
    [Fact]
    public async Task PlantedFixationOwnershipAndReplayAreFoundWithCompletePathsAndCost()
    {
        using var repository = await FixtureRepository.CreateAsync(TestContext.Current.CancellationToken);
        var inventory = await InventoryAsync(repository.Root);
        var agent = new QueueAgent(
            FindingResponse("sessionLifecycle", "high", 9, 12,
                ("entry", 4, "MapPost /login"),
                ("authentication", 11, "Login"),
                ("persistence", 12, "SessionStore.Save"),
                ("response", 13, "Login response")),
            FindingResponse("horizontalPrivilegeEscalation", "critical", 16, 18,
                ("entry", 5, "MapPost /accounts/{accountId}/transfer"),
                ("authorization", 16, "Transfer"),
                ("persistence", 18, "Accounts.Get without owner predicate"),
                ("stateTransition", 19, "Account.Debit"),
                ("persistence", 20, "Accounts.Save"),
                ("response", 21, "Transfer response")),
            FindingResponse("replay", "high", 24, 27,
                ("entry", 6, "MapPost /orders/{orderId}/charge"),
                ("authorization", 24, "Charge"),
                ("persistence", 27, "Payments.Capture"),
                ("persistence", 29, "Orders.Save"),
                ("response", 30, "Charge response")));
        var runner = new FlowReviewRunner(agent, clock: () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

        var fixation = await runner.ReviewAsync(Request(repository.Root, inventory, "/login", "login"),
            TestContext.Current.CancellationToken);
        var ownership = await runner.ReviewAsync(Request(repository.Root, inventory, "/accounts/{accountId}/transfer", "transfer"),
            TestContext.Current.CancellationToken);
        var replay = await runner.ReviewAsync(Request(repository.Root, inventory, "/orders/{orderId}/charge", "charge"),
            TestContext.Current.CancellationToken);

        AssertFinding(fixation.Report, BusinessLogicClass.SessionLifecycle, 4, 2);
        AssertFinding(ownership.Report, BusinessLogicClass.HorizontalPrivilegeEscalation, 6, 2);
        AssertFinding(replay.Report, BusinessLogicClass.Replay, 5, 2);
        Assert.All(new[] { fixation, ownership, replay }, result =>
        {
            Assert.Equal(FlowReviewVerdict.Fail, result.Report.Verdict);
            Assert.Equal("claude-sonnet-4-5", result.Report.Provenance.Model);
            Assert.Equal("resolved", result.Report.Provenance.Cost.Status);
            Assert.NotNull(result.Report.Provenance.Cost.Amount);
            Assert.True(result.Report.Provenance.Cost.Amount > 0);
            Assert.Equal(0.012825m, result.Report.Provenance.Cost.Amount);
            Assert.Equal("USD", result.Report.Provenance.Cost.Currency);
            Assert.True(File.Exists(result.ReportPath));
        });

        var usage = await UsageLedger.QueryAsync(repository.Root, kind: FlowReviewRunner.UsageKind,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, usage.Runs);
        Assert.All(usage.Recent, entry => Assert.Equal("flow", entry.Level));

        using var generated = JsonDocument.Parse(await File.ReadAllTextAsync(
            fixation.ReportPath!, TestContext.Current.CancellationToken));
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", "flow-review.v1.schema.json"),
            TestContext.Current.CancellationToken));
        var validation = schema.Evaluate(generated.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }

    [Fact]
    public async Task ExternalDecisiveLogicIsUndeterminedAndNotSilentlyPassed()
    {
        using var repository = await FixtureRepository.CreateAsync(TestContext.Current.CancellationToken);
        var inventory = await InventoryAsync(repository.Root);
        var response = """
        {
          "verdict": "undetermined",
          "summary": "The repository delegates assertion validation to an external identity provider.",
          "undeterminedReason": "ExternalIdentityProvider.Validate and its runtime issuer/audience/replay policy are outside the repository.",
          "findings": []
        }
        """;

        var result = await new FlowReviewRunner(new QueueAgent(response)).ReviewAsync(
            Request(repository.Root, inventory, "/sso/callback", "sso"), TestContext.Current.CancellationToken);

        Assert.Equal(FlowReviewVerdict.Undetermined, result.Report.Verdict);
        Assert.Contains("outside the repository", result.Report.UndeterminedReason);
        Assert.Empty(result.Report.Findings);
    }

    [Fact]
    public async Task Enabled_domain_dual_write_retains_flow_report_and_common_observation()
    {
        using var repository = await FixtureRepository.CreateAsync(TestContext.Current.CancellationToken);
        var inventory = await InventoryAsync(repository.Root);
        var runner = new FlowReviewRunner(
            new QueueAgent("""
                {"verdict":"pass","summary":"The complete flow is sound.","findings":[]}
                """),
            qualityTaxonomyOptions: new QualityTaxonomyOptions { ObservationWriteEnabled = true });

        var result = await runner.ReviewAsync(
            Request(repository.Root, inventory, "/login", "login"), TestContext.Current.CancellationToken);
        var observations = await new QualityObservationStore(repository.Root)
            .ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.ReportPath));
        var observation = Assert.Single(observations.Observations);
        Assert.Equal("flow-business-logic-review", observation.Profile.Id);
        Assert.Equal(QualityAssessment.Pass, observation.Assessment);
        Assert.Equal(result.Report.Provenance.RunId, observation.Producer.RunId);
        Assert.Equal("anthropic", observation.Producer.Provider);
        Assert.Equal("claude-sonnet-4-5", observation.Producer.RequestedModel);
        Assert.Equal("high", observation.Producer.ThinkingLevel);
        Assert.Equal("2026-07-24", observation.Producer.RoutePolicyVersion);
    }

    [Fact]
    public async Task FalsePositiveDispositionRemainsVisibleAndCountedOnNextReview()
    {
        using var repository = await FixtureRepository.CreateAsync(TestContext.Current.CancellationToken);
        var inventory = await InventoryAsync(repository.Root);
        var response = FindingResponse("replay", "high", 24, 27,
            ("entry", 6, "route"),
            ("authorization", 24, "Charge"),
            ("persistence", 27, "Capture"),
            ("response", 30, "response"));
        var runner = new FlowReviewRunner(new QueueAgent(response, response));
        var request = Request(repository.Root, inventory, "/orders/{orderId}/charge", "charge");
        var first = await runner.ReviewAsync(request, TestContext.Current.CancellationToken);
        var fingerprint = Assert.Single(first.Report.Findings).Fingerprint;
        await new FindingStateStore(repository.Root).SetAsync(
            fingerprint, FindingState.FalsePositive, "security-reviewer",
            "Payment provider enforces the idempotency key added by deployment middleware.",
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await runner.ReviewAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Report.FindingCounts.FalsePositive);
        Assert.Equal(FindingState.FalsePositive, Assert.Single(second.Report.Findings).State);
        var json = await File.ReadAllTextAsync(second.ReportPath!, TestContext.Current.CancellationToken);
        Assert.Contains("\"falsePositive\": 1", json);
        Assert.Contains("\"state\": \"falsePositive\"", json);
    }

    [Fact]
    public async Task SourceOrBoundaryCatalogueChangesMakeFlowConclusionStale()
    {
        using var repository = await FixtureRepository.CreateAsync(TestContext.Current.CancellationToken);
        var inventory = await InventoryAsync(repository.Root);
        var request = Request(repository.Root, inventory, "/sso/callback", "sso", persist: false);
        var result = await new FlowReviewRunner(new QueueAgent("""
        {"verdict":"undetermined","summary":"External policy unavailable.","undeterminedReason":"Provider policy is outside the repository.","findings":[]}
        """)).ReviewAsync(request, TestContext.Current.CancellationToken);

        await File.AppendAllTextAsync(Path.Combine(repository.Root, FixtureRepository.RelativeSource), "\n// changed\n",
            TestContext.Current.CancellationToken);
        var sourceStaleness = await new FlowReviewRunner(new QueueAgent()).EvaluateStalenessAsync(
            request, result.Report, TestContext.Current.CancellationToken);
        Assert.True(sourceStaleness.Stale);
        Assert.Contains(sourceStaleness.Reasons, reason => reason.Contains("Flow source", StringComparison.Ordinal));

        var changedInventory = inventory with { SensorVersion = "catalogue-moved" };
        var catalogueStaleness = await new FlowReviewRunner(new QueueAgent()).EvaluateStalenessAsync(
            request with { BoundaryInventory = changedInventory }, result.Report, TestContext.Current.CancellationToken);
        Assert.Contains(catalogueStaleness.Reasons, reason => reason.Contains("Boundary catalogue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsFindingThatDoesNotRecordEntryToOutcomePath()
    {
        using var repository = await FixtureRepository.CreateAsync(TestContext.Current.CancellationToken);
        var inventory = await InventoryAsync(repository.Root);
        var response = FindingResponse("replay", "high", 24, 27,
            ("persistence", 27, "Capture"),
            ("response", 30, "response"));

        var exception = await Assert.ThrowsAsync<ReviewResponseException>(() =>
            new FlowReviewRunner(new QueueAgent(response)).ReviewAsync(
                Request(repository.Root, inventory, "/orders/{orderId}/charge", "charge"),
                TestContext.Current.CancellationToken));

        Assert.Contains("start at entry", exception.Message);
    }

    private static FlowReviewRequest Request(
        string root,
        BoundaryInventory inventory,
        string route,
        string id,
        bool persist = true)
    {
        var boundary = inventory.Entries.Single(entry => entry.Name.EndsWith(route, StringComparison.Ordinal));
        return new FlowReviewRequest(
            root,
            new FlowDefinition(id, id, $"Complete {id} flow.", [boundary.Id],
                id == "sso" ? ["ExternalIdentityProvider"] : null),
            inventory,
            "Session(sessionId,userId); Account(id,ownerId,balance); Order(id,total,paid);",
            $"{boundary.Name} -> handler -> repository/provider -> response",
            [FixtureRepository.RelativeSource],
            persist);
    }

    private static void AssertFinding(
        FlowReviewReport report,
        BusinessLogicClass expectedClass,
        int expectedSteps,
        int weakest)
    {
        var finding = Assert.Single(report.Findings);
        Assert.Equal(expectedClass, finding.Class);
        Assert.Equal(expectedSteps, finding.FlowPath.Count);
        Assert.Equal(weakest, finding.WeakestPointIndex);
        Assert.Equal(FlowPathStage.Entry, finding.FlowPath[0].Stage);
        Assert.Equal(FlowPathStage.Response, finding.FlowPath[^1].Stage);
        Assert.Equal(FixtureRepository.RelativeSource, finding.FlowPath[weakest].Path);
    }

    private static async Task<BoundaryInventory> InventoryAsync(string root) =>
        await new BoundaryInventorySensor().InventoryAsync(
            new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);

    private static string FindingResponse(
        string findingClass,
        string severity,
        int handlerLine,
        int weakestLine,
        params (string Stage, int Line, string Action)[] steps)
    {
        var path = new JsonArray(steps.Select((step, index) => (JsonNode)new JsonObject
        {
            ["order"] = index,
            ["stage"] = step.Stage,
            ["path"] = FixtureRepository.RelativeSource,
            ["line"] = step.Line,
            ["symbol"] = index == 0 ? "route" : $"step-{index}",
            ["action"] = step.Action,
        }).ToArray());
        var weakestIndex = Array.FindIndex(steps, step => step.Line == weakestLine);
        if (weakestIndex < 0) weakestIndex = Array.FindIndex(steps, step => step.Line == handlerLine);
        return new JsonObject
        {
            ["verdict"] = "fail",
            ["summary"] = $"Planted {findingClass} issue found.",
            ["findings"] = new JsonArray(new JsonObject
            {
                ["class"] = findingClass,
                ["severity"] = severity,
                ["title"] = $"Planted {findingClass}",
                ["description"] = "The complete path violates its security invariant.",
                ["recommendation"] = "Enforce the invariant atomically at the weakest point.",
                ["weakestPointIndex"] = weakestIndex,
                ["flowPath"] = path,
            }),
        }.ToJsonString();
    }

    private sealed class QueueAgent(params string[] responses) : IReviewAgent
    {
        private readonly Queue<string> responses = new(responses);
        public string AgentName => "fixture-agent";
        public string? Model => "claude-sonnet-4-5";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            if (responses.Count == 0) throw new InvalidOperationException("No fixture response remains.");
            Assert.Contains("Boundary inventory", prompt);
            Assert.Contains("Call graph", prompt);
            return Task.FromResult(new ReviewAgentResult(
                "flow-" + Guid.NewGuid().ToString("N"),
                responses.Dequeue(),
                new TokenUsage(2_000, 500, 250, 100, 800),
                "claude-sonnet-4-5",
                "anthropic",
                "high",
                "2026-07-24"));
        }
    }

    private sealed class FixtureRepository : IDisposable
    {
        public const string RelativeSource = "src/FixtureService.cs";
        private FixtureRepository(string root) => Root = root;
        public string Root { get; }

        public static async Task<FixtureRepository> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "quality-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flow-review", "FixtureService.cs.txt");
            await using var source = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            await using var destination = new FileStream(Path.Combine(root, RelativeSource), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await source.CopyToAsync(destination, cancellationToken);
            return new FixtureRepository(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
