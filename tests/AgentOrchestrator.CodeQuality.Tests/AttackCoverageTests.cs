using Json.Schema;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AttackCoverageTests
{
    [Fact]
    public async Task Matrix_has_an_explicit_cell_for_every_applicable_pair()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-complete-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                app.MapGet("/one", One);
                queue.Consume(Handle);
                """, TestContext.Current.CancellationToken);
            var inventory = Inventory(
                Boundary("one", "http", 1),
                Boundary("queue", "message-consumer", 2));
            var catalogue = Catalogue(
                Entry("http-only", AttackSeverity.Medium, "http"),
                Entry("both", AttackSeverity.Medium, "http", "message-consumer"));

            var matrix = await new AttackCoverageService().BuildAsync(
                root, inventory, catalogue, recheckDeterministic: false,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, matrix.CellCount);
            Assert.Equal(3, matrix.NotYetCheckedCount);
            Assert.All(matrix.Rows.SelectMany(row => row.Cells), cell =>
            {
                Assert.Equal(AttackCoverageVerdict.NotYetChecked, cell.Verdict);
                Assert.Contains("explicit work item", cell.Reason, StringComparison.Ordinal);
            });
            Assert.DoesNotContain(matrix.Rows.Single(row => row.Boundary.Id == "queue").Cells,
                cell => cell.AttackId == "http-only");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Project_catalogue_overrides_global_by_id_and_can_extend_the_seed()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-catalogue-precedence-").FullName;
        var global = Directory.CreateTempSubdirectory("quality-studio-global-catalogue-").FullName;
        try
        {
            var globalEntry = Entry("project-specific", AttackSeverity.Low, "http") with { Title = "Global" };
            await File.WriteAllTextAsync(
                Path.Combine(global, AttackCatalogueResolver.GlobalFileName),
                System.Text.Json.JsonSerializer.Serialize(
                    new AttackCatalogueDocument("test", 1, "global-1", [globalEntry]),
                    AttackCoverageJson.Options),
                TestContext.Current.CancellationToken);
            var projectEntry = globalEntry with { Version = "2.0.0", Title = "Project" };
            var projectPath = Path.Combine(root,
                AttackCatalogueResolver.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            await File.WriteAllTextAsync(
                projectPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new AttackCatalogueDocument("test", 1, "project-2", [projectEntry]),
                    AttackCoverageJson.Options),
                TestContext.Current.CancellationToken);

            var resolved = new AttackCatalogueResolver().Resolve(root, global);

            var effective = Assert.Single(resolved.Entries,
                item => item.Entry.Id == "project-specific");
            Assert.Equal("Project", effective.Entry.Title);
            Assert.Equal("project", effective.Scope);
            Assert.Contains("global:global-1", resolved.Version, StringComparison.Ordinal);
            Assert.Contains("project:project-2", resolved.Version, StringComparison.Ordinal);
            Assert.Contains(resolved.Entries, item => item.Entry.Id == "OWASP-API1-BOLA");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(global, true);
        }
    }

    [Fact]
    public async Task Built_in_catalogue_conforms_to_the_repository_schema()
    {
        var root = FindRepositoryRoot();
        using var catalogue = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "src", "AgentOrchestrator.CodeQuality", "catalogues",
                "attack-catalogue.v1.json"),
            TestContext.Current.CancellationToken));
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(root, "schemas", "attack-catalogue.v1.schema.json"),
            TestContext.Current.CancellationToken));

        var result = schema.Evaluate(catalogue.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public async Task QualityStudio_Api_has_complete_mechanical_or_explicitly_deferred_coverage()
    {
        var sourceRoot = FindRepositoryRoot();
        var root = Directory.CreateTempSubdirectory("quality-studio-api-coverage-").FullName;
        try
        {
            var relativeProject = Path.Combine("src", "QualityStudio.Api");
            var targetProject = Path.Combine(root, relativeProject);
            Directory.CreateDirectory(targetProject);
            foreach (var source in Directory.EnumerateFiles(
                         Path.Combine(sourceRoot, relativeProject), "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(source, Path.Combine(targetProject, Path.GetFileName(source)));
            }
            var inventory = await new BoundaryInventorySensor().InventoryAsync(
                new SensorScanRequest(root, SensorScope.Path, "src/QualityStudio.Api", PersistMetadata: false),
                TestContext.Current.CancellationToken);
            var catalogue = new AttackCatalogueResolver().Resolve(root);

            var matrix = await new AttackCoverageService().BuildAsync(
                root, inventory, catalogue, "src/QualityStudio.Api", recheckDeterministic: true,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEmpty(matrix.Rows);
            Assert.Equal(
                inventory.Entries.Sum(boundary => catalogue.Entries.Count(attack =>
                    AttackCatalogueResolver.Applies(attack.Entry, boundary))),
                matrix.CellCount);
            Assert.All(matrix.Rows.SelectMany(row => row.Cells), cell =>
                Assert.True(cell.Verdict == AttackCoverageVerdict.NotYetChecked ||
                            cell.Provenance.Count > 0));
            Assert.Contains(matrix.Rows.SelectMany(row => row.Cells),
                cell => cell.Provenance.Any(item =>
                    item.Source == AttackCoverageSource.DeterministicSensor &&
                    item.TokenCost.TotalTokens == 0));
            Assert.Contains(matrix.Rows.SelectMany(row => row.Cells),
                cell => cell.Verdict == AttackCoverageVerdict.NotYetChecked);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Judged_cell_carries_complete_provenance_and_catalogue_drift_is_entry_scoped()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-catalogue-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"),
                "app.MapGet(\"/one\", One);", TestContext.Current.CancellationToken);
            var inventory = Inventory(Boundary("one", "http", 1));
            var first = Entry("attack-one", AttackSeverity.Medium, "http");
            var second = Entry("attack-two", AttackSeverity.Medium, "http");
            var catalogue = Catalogue(first, second);
            var service = new AttackCoverageService(() => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
            foreach (var attack in new[] { first, second })
            {
                await service.RecordAsync(root, inventory, catalogue,
                    Submission("one", attack.Id, "assessment-" + attack.Id, AttackCoverageVerdict.Pass),
                    TestContext.Current.CancellationToken);
            }

            var changedFirst = first with { Version = "1.1.0", Description = "Changed test description." };
            var bumped = Catalogue("test:2.0.0", changedFirst, second);
            var matrix = await service.BuildAsync(root, inventory, bumped, recheckDeterministic: false,
                cancellationToken: TestContext.Current.CancellationToken);

            var changed = Cell(matrix, "one", first.Id);
            Assert.Equal([AttackCoverageStalenessReason.CatalogueChanged], changed.StalenessReasons);
            Assert.Equal(AttackCoverageVerdict.Pass, changed.Verdict);
            Assert.Single(changed.Provenance);
            var provenance = changed.Provenance[0];
            Assert.Equal("review-agent", provenance.Reviewer.Agent);
            Assert.Equal("test-model", provenance.Reviewer.Model);
            Assert.Equal("high", provenance.Reviewer.ThinkingLevel);
            Assert.NotEmpty(provenance.PromptVersion);
            Assert.StartsWith("sha256:", provenance.PromptHash, StringComparison.Ordinal);
            Assert.StartsWith("sha256:", provenance.BoundaryDefinitionHash, StringComparison.Ordinal);
            Assert.StartsWith("sha256:", provenance.CoveredCodeHash, StringComparison.Ordinal);
            Assert.Equal(30, provenance.TokenCost.TotalTokens);
            Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), provenance.CheckedAt);

            var untouched = Cell(matrix, "one", second.Id);
            Assert.Empty(untouched.StalenessReasons);
            Assert.Equal(provenance.CoveredCodeHash, untouched.Provenance[0].CoveredCodeHash);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Changing_one_endpoint_marks_only_its_row_code_stale()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-code-").FullName;
        var source = Path.Combine(root, "Program.cs");
        try
        {
            await File.WriteAllTextAsync(source, """
                app.MapGet("/one", One);
                app.MapGet("/two", Two);
                """, TestContext.Current.CancellationToken);
            var inventory = Inventory(Boundary("one", "http", 1), Boundary("two", "http", 2));
            var catalogue = Catalogue(Entry("attack", AttackSeverity.Medium, "http"));
            var service = new AttackCoverageService();
            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "one-pass", AttackCoverageVerdict.Pass),
                TestContext.Current.CancellationToken);
            await service.RecordAsync(root, inventory, catalogue,
                Submission("two", "attack", "two-pass", AttackCoverageVerdict.Pass),
                TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(source, """
                app.MapGet("/one", OneWithNewControl);
                app.MapGet("/two", Two);
                """, TestContext.Current.CancellationToken);
            var matrix = await service.BuildAsync(root, inventory, catalogue, recheckDeterministic: false,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal([AttackCoverageStalenessReason.CodeChanged],
                Cell(matrix, "one", "attack").StalenessReasons);
            Assert.Empty(Cell(matrix, "two", "attack").StalenessReasons);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Pass_finding_pass_is_preserved_as_a_commit_trajectory()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-history-").FullName;
        var at = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"),
                "app.MapGet(\"/one\", One);", TestContext.Current.CancellationToken);
            var inventory = Inventory(Boundary("one", "http", 1));
            var catalogue = Catalogue(Entry("attack", AttackSeverity.Medium, "http"));
            var service = new AttackCoverageService(() => at);

            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "a1", AttackCoverageVerdict.Pass) with
                { Commit = "c1", CommitRange = "base..c1" }, TestContext.Current.CancellationToken);
            at = at.AddDays(1);
            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "a2", AttackCoverageVerdict.Finding) with
                {
                    FindingFingerprint = "sha256:" + new string('b', 64),
                    Commit = "c2",
                    CommitRange = "c1..c2",
                }, TestContext.Current.CancellationToken);
            at = at.AddDays(1);
            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "a3", AttackCoverageVerdict.Pass) with
                { Commit = "c3", CommitRange = "c2..c3" }, TestContext.Current.CancellationToken);

            var cell = Cell(await service.BuildAsync(root, inventory, catalogue, recheckDeterministic: false,
                cancellationToken: TestContext.Current.CancellationToken), "one", "attack");

            Assert.Equal(AttackCoverageVerdict.Pass, cell.Verdict);
            Assert.Equal(
                [AttackCoverageVerdict.Pass, AttackCoverageVerdict.Finding, AttackCoverageVerdict.Pass],
                cell.History.Select(item => item.Verdict));
            Assert.Equal(["base..c1", "c1..c2", "c2..c3"],
                cell.History.Select(item => item.CommitRange));
            var findingStates = await new FindingStateStore(root).ReadAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(FindingState.Resolved,
                findingStates["sha256:" + new string('b', 64)].State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Independent_high_severity_disagreement_is_not_averaged_away()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-disagreement-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"),
                "app.MapGet(\"/one\", One);", TestContext.Current.CancellationToken);
            var inventory = Inventory(Boundary("one", "http", 1));
            var catalogue = Catalogue(Entry("attack", AttackSeverity.High, "http"));
            var service = new AttackCoverageService();
            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "shared", AttackCoverageVerdict.Pass),
                TestContext.Current.CancellationToken);
            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "shared", AttackCoverageVerdict.Finding) with
                {
                    Reviewer = new AttackReviewerIdentity("independent-agent", "test-model-2", "high"),
                    FindingFingerprint = "sha256:" + new string('a', 64),
                }, TestContext.Current.CancellationToken);

            var cell = Cell(await service.BuildAsync(root, inventory, catalogue, recheckDeterministic: false,
                cancellationToken: TestContext.Current.CancellationToken), "one", "attack");

            Assert.Equal(AttackCoverageVerdict.Finding, cell.Verdict);
            Assert.True(cell.Disagreement);
            Assert.True(cell.NeedsHumanAttention);
            Assert.Equal(2, cell.RequiredJudgements);
            Assert.Equal(2, cell.IndependentJudgements);
            Assert.Equal("low", cell.Confidence);
            Assert.Contains("disagree", cell.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Current_deterministic_result_overrides_a_later_contradicting_agent_claim()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-coverage-override-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"),
                "app.MapGet(\"/one\", One);", TestContext.Current.CancellationToken);
            var inventory = Inventory(Boundary("one", "http", 1));
            var entry = Entry("attack", AttackSeverity.High, "http") with
            {
                DeterministicRuleIds = ["boundary/missing-authorization"],
                DeterministicPassConclusive = true,
            };
            var catalogue = Catalogue(entry);
            var service = new AttackCoverageService();
            var initial = await service.BuildAsync(root, inventory, catalogue,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(AttackCoverageVerdict.Pass, Cell(initial, "one", "attack").Verdict);
            await service.RecordAsync(root, inventory, catalogue,
                Submission("one", "attack", "agent-claim", AttackCoverageVerdict.Finding) with
                { FindingFingerprint = "sha256:" + new string('c', 64) },
                TestContext.Current.CancellationToken);

            var cell = Cell(await service.BuildAsync(root, inventory, catalogue,
                cancellationToken: TestContext.Current.CancellationToken), "one", "attack");

            Assert.Equal(AttackCoverageVerdict.Pass, cell.Verdict);
            Assert.True(cell.DeterministicOverride);
            Assert.True(cell.Disagreement);
            Assert.Contains(cell.Provenance, item => item.Source == AttackCoverageSource.DeterministicSensor);
            Assert.Contains(cell.Provenance, item => item.Source == AttackCoverageSource.Agent);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static AttackCoverageCell Cell(AttackCoverageMatrix matrix, string boundary, string attack) =>
        matrix.Rows.Single(row => row.Boundary.Id == boundary).Cells.Single(cell => cell.AttackId == attack);

    private static AttackJudgementSubmission Submission(
        string boundary,
        string attack,
        string assessment,
        AttackCoverageVerdict verdict) =>
        new(
            assessment,
            boundary,
            attack,
            verdict,
            verdict == AttackCoverageVerdict.Finding ? "The attack is exploitable." : "The control blocks the attack.",
            [new AttackEvidence("test", "Program.cs", "Exact test input.")],
            [],
            null,
            null,
            AttackCoverageSource.Agent,
            new AttackReviewerIdentity("review-agent", "test-model", "high"),
            new AttackTokenCost(20, 10),
            null,
            null);

    private static BoundaryInventory Inventory(params BoundaryEntry[] boundaries) =>
        new("test", 1, "boundaries", "1.0.0", boundaries, []);

    private static BoundaryEntry Boundary(string id, string kind, int line) =>
        new(
            id,
            kind,
            "inbound",
            id,
            "http",
            new BoundarySourceLocation("Program.cs", line),
            new BoundaryFact("public", ["test"]),
            new BoundaryFact("required", ["test"]),
            new BoundaryFact("policy", ["test"]),
            [],
            new BoundaryResponse("json", "application/json"),
            [],
            new BoundaryLimit("global", ["test"]),
            new BoundaryLimit("global", ["test"]),
            [],
            []);

    private static AttackCatalogueEntry Entry(
        string id,
        AttackSeverity severity,
        params string[] kinds) =>
        new(
            id,
            "1.0.0",
            id,
            "Test attack.",
            new AttackApplicability(kinds),
            ["Test evidence."],
            severity,
            "Test severity frame.",
            []);

    private static ResolvedAttackCatalogue Catalogue(params AttackCatalogueEntry[] entries) =>
        Catalogue("test:1.0.0", entries);

    private static ResolvedAttackCatalogue Catalogue(
        string version,
        params AttackCatalogueEntry[] entries) =>
        new(
            version,
            entries.Select(entry => new ResolvedAttackCatalogueEntry(
                entry, "test", "test", version, AttackCoverageJson.Hash(entry))).ToArray(),
            ["test"]);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new DirectoryNotFoundException("Quality Studio repository root was not found.");
    }
}
