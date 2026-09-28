using System.Text.Json;
using System.Text.Json.Nodes;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Analyzer rule ids as catalogue citizens: mapped to named rules through <c>deterministicRuleIds</c>,
/// persisted in the data root, and handed to the review agent with the rule they enforce.
/// </summary>
public sealed class AnalyzerCatalogueLinkTests
{
    [Theory]
    [InlineData("CA2016", "QS-CS-003")]
    [InlineData("ca2016", "QS-CS-003")]
    [InlineData("CA3003", "QS-CS-005")]
    [InlineData("CA2326", "QS-CS-008")]
    [InlineData("@angular-eslint/template/no-call-expression", "QS-NG-010")]
    [InlineData("@angular-eslint/prefer-on-push-component-change-detection", "QS-NG-005")]
    [InlineData("NG8102", "QS-NG-004")]
    [InlineData("no-eval", "QS-GN-001")]
    [InlineData("architecture/missing-directory", "QS-GN-003")]
    // Family entries: an id no specific rule claims falls to the opt-in family rule.
    [InlineData("CA1822", "QS-CS-013")]
    [InlineData("CS8618", "QS-CS-013")]
    [InlineData("TS2322", "QS-NG-014")]
    public void The_built_in_catalogue_maps_analyzer_ids_to_named_rules(string analyzerId, string ruleId)
    {
        var map = DeterministicRuleMap.From(ResolveBuiltIn());

        var link = Assert.Single(map.For(analyzerId));

        Assert.Equal(ruleId, link.Id);
    }

    [Fact]
    public void An_exact_id_wins_over_a_family_and_unknown_ids_map_to_nothing()
    {
        var map = DeterministicRuleMap.From(ResolveBuiltIn());

        Assert.Equal("QS-CS-003", Assert.Single(map.For("CA2016")).Id);
        Assert.Empty(map.For("S1234"));
        Assert.Empty(map.For(null));
    }

    [Fact]
    public void Family_rules_are_off_by_default_and_a_project_override_turns_one_on()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rule-map-").FullName;
        try
        {
            Assert.False(Assert.Single(DeterministicRuleMap.From(ResolveBuiltIn()).For("TS2322")).Enabled);
            var overrides = Path.Combine(root, ".quality", "rules", "overrides.json");
            Directory.CreateDirectory(Path.GetDirectoryName(overrides)!);
            File.WriteAllText(overrides, """
                { "schemaVersion": 1, "overrides": [ { "id": "QS-NG-014", "enabled": true, "reason": "The workspace is type-clean." } ] }
                """);

            var map = DeterministicRuleMap.From(new RuleCatalogueResolver().Resolve(root));

            Assert.True(Assert.Single(map.For("TS2322")).Enabled);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Every_rule_listing_analyzer_ids_carries_a_non_empty_unique_list()
    {
        foreach (var rule in ResolveBuiltIn().Rules.Where(rule => rule.Rule.DeterministicRuleIds.Count > 0))
        {
            Assert.All(rule.Rule.DeterministicRuleIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.Equal(
                rule.Rule.DeterministicRuleIds.Count,
                rule.Rule.DeterministicRuleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void The_prompt_evidence_names_the_enabled_rule_a_finding_enforces_when_the_review_carries_it()
    {
        var evidence = new[]
        {
            Result("roslyn", Finding("CA2016", "src/A.cs"), Finding("CA1822", "src/A.cs")),
        };
        var map = DeterministicRuleMap.From(ResolveBuiltIn());

        var withRule = JsonNode.Parse(DeterministicEvidenceProjection.ToPromptJson(
            evidence, map, new HashSet<string>(["QS-CS-003"], StringComparer.OrdinalIgnoreCase)))!;
        var withoutRule = JsonNode.Parse(DeterministicEvidenceProjection.ToPromptJson(
            evidence, map, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))!;

        var findings = withRule[0]!["findings"]!.AsArray();
        var ca2016 = findings.Single(finding => finding!["ruleId"]!.GetValue<string>() == "CA2016")!;
        var ca1822 = findings.Single(finding => finding!["ruleId"]!.GetValue<string>() == "CA1822")!;
        Assert.Equal("QS-CS-003", ca2016["catalogueRuleIds"]![0]!.GetValue<string>());
        // CA1822 falls to QS-CS-013, which is off by default, so the agent is not pointed at it.
        Assert.Null(ca1822["catalogueRuleIds"]);
        Assert.DoesNotContain("catalogueRuleIds", withoutRule.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("catalogueRuleIds", DeterministicEvidenceProjection.ToPromptJson(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_explains_catalogue_links_only_when_evidence_carries_them()
    {
        var builder = new ReviewPromptBuilder();

        var linked = builder.Build("src/A.cs", "code", "", "", "class A {}", null, null, ReviewLevel.File, null,
            """[{"sensorId":"roslyn","findings":[{"ruleId":"CA2016","catalogueRuleIds":["QS-CS-003"]}]}]""");
        var plain = builder.Build("src/A.cs", "code", "", "", "class A {}", null, null, ReviewLevel.File, null,
            """[{"sensorId":"roslyn","findings":[{"ruleId":"CA1822"}]}]""");

        Assert.Contains("deterministic check of that named rule", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("deterministic check of that named rule", plain, StringComparison.Ordinal);
        // Without links the section renders exactly as before, so unlinked prompts keep their hash.
        Assert.Contains("judgement; analyzer evidence does not set or cap it.\n\n```json[", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_store_persists_results_outside_the_checkout_and_links_rules_per_file()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-analyzer-store-").FullName;
        try
        {
            var store = new AnalyzerResultStore(root);

            await store.RecordAsync(
                Result("roslyn", Finding("CA2016", "src/A.cs"), Finding("CA1822", "src/Deep/B.cs")) with { SuppressedFindings = 3 },
                TestContext.Current.CancellationToken);

            Assert.StartsWith(QualityDataRootFixture.BaseDirectory, store.Directory, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality")));
            var view = await store.ReadForPathAsync(
                "src/A.cs", DeterministicRuleMap.From(ResolveBuiltIn()), TestContext.Current.CancellationToken);
            var entry = Assert.Single(view.Findings);
            Assert.Equal("roslyn", entry.SensorId);
            Assert.Equal("QS-CS-003", Assert.Single(entry.CatalogueRules).Id);
            var sensor = Assert.Single(view.Sensors);
            Assert.Equal(2, sensor.Findings);
            Assert.Equal(3, sensor.SuppressedFindings);
            var folder = await store.ReadForPathAsync("src", DeterministicRuleMap.Empty, TestContext.Current.CancellationToken);
            Assert.Equal(2, folder.Findings.Count);
            var counts = await store.CountByFileAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, counts["src/A.cs"]);
            Assert.Equal(1, counts["src/Deep/B.cs"]);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_path_scan_replaces_only_its_path_and_an_unavailable_scan_keeps_the_last_result()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-analyzer-merge-").FullName;
        try
        {
            var store = new AnalyzerResultStore(root);
            await store.RecordAsync(
                Result("eslint", Finding("no-eval", "frontend/a.ts"), Finding("no-eval", "frontend/b/c.ts")),
                TestContext.Current.CancellationToken);

            await store.RecordAsync(
                Result("eslint", "path", "frontend/b", Finding("no-new-func", "frontend/b/c.ts", line: 9)),
                TestContext.Current.CancellationToken);
            var merged = await store.ReadForPathAsync(".", DeterministicRuleMap.Empty, TestContext.Current.CancellationToken);
            Assert.Equal(
                ["frontend/a.ts:no-eval", "frontend/b/c.ts:no-new-func"],
                merged.Findings.Select(entry => $"{entry.Finding.Locations[0].Path}:{entry.Finding.RuleId}"));

            await store.RecordAsync(
                new SensorScanResult(false, "eslint/bin/eslint.js is not installed", [], Provenance("eslint", "repository", ".")),
                TestContext.Current.CancellationToken);
            var kept = await store.ReadForPathAsync(".", DeterministicRuleMap.Empty, TestContext.Current.CancellationToken);
            Assert.Equal(2, kept.Findings.Count);
            var sensor = Assert.Single(kept.Sensors);
            Assert.True(sensor.Available);
            Assert.False(sensor.LastAttempt.Available);
            Assert.Contains("not installed", sensor.UnavailableReason, StringComparison.Ordinal);

            await store.RecordAsync(Result("eslint"), TestContext.Current.CancellationToken);
            Assert.Empty((await store.ReadForPathAsync(".", DeterministicRuleMap.Empty, TestContext.Current.CancellationToken)).Findings);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_torn_document_is_skipped_instead_of_failing_the_file_view()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-analyzer-torn-").FullName;
        try
        {
            var store = new AnalyzerResultStore(root);
            Directory.CreateDirectory(store.Directory);
            File.WriteAllText(Path.Combine(store.Directory, "roslyn.json"), "{ \"schemaVersion\": 1, ");

            var view = await store.ReadForPathAsync(".", DeterministicRuleMap.Empty, TestContext.Current.CancellationToken);

            Assert.Empty(view.Sensors);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Evidence_collected_for_a_review_is_persisted_when_asked()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-analyzer-collect-").FullName;
        try
        {
            var registry = new SensorRegistry([new FixedSensor(Result("sarif", Finding("CA2016", "src/A.cs")))]);

            await new DeterministicEvidenceCollector(registry, persistResults: true).CollectAsync(
                root, [new ReviewSensorConfiguration("sarif")], TestContext.Current.CancellationToken);

            var counts = await new AnalyzerResultStore(root).CountByFileAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, counts["src/A.cs"]);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_persisted_result_round_trips_through_the_review_finding_contract()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-analyzer-roundtrip-").FullName;
        try
        {
            var store = new AnalyzerResultStore(root);
            var original = Finding("CA2016", "src/A.cs");
            await store.RecordAsync(Result("roslyn", original), TestContext.Current.CancellationToken);

            var json = File.ReadAllText(Path.Combine(store.Directory, "roslyn.json"));
            var read = Assert.Single((await store.ReadAllAsync(TestContext.Current.CancellationToken))[0].Result!.Findings);

            Assert.Equal(original.Fingerprint, read.Fingerprint);
            Assert.Equal(FindingSourceKind.Deterministic, read.Source!.Kind);
            Assert.Equal("medium", JsonDocument.Parse(json).RootElement
                .GetProperty("result").GetProperty("findings")[0].GetProperty("severity").GetString());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    private static ResolvedRuleCatalogue ResolveBuiltIn()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-studio-no-overrides-" + Guid.NewGuid().ToString("N"));
        return new RuleCatalogueResolver().Resolve(root);
    }

    private static SensorScanResult Result(string sensorId, params ReviewFinding[] findings) =>
        Result(sensorId, "repository", ".", findings);

    private static SensorScanResult Result(string sensorId, string scope, string target, params ReviewFinding[] findings) =>
        new(true, null, findings, Provenance(sensorId, scope, target));

    private static SensorProvenance Provenance(string sensorId, string scope, string target) =>
        new(sensorId, "1.0.0", scope, target, "2026-09-28T08:00:00.0000000Z",
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static ReviewFinding Finding(string ruleId, string path, int line = 3) => new(
        $"{ruleId.ToLowerInvariant()}-{path.GetHashCode():x8}",
        "analyzer",
        FindingSeverity.Medium,
        $"{ruleId} title",
        $"{ruleId} message",
        "Fix it.",
        [new FindingLocation(path, new FindingRange(new FindingPosition(line, 1), new FindingPosition(line, 5)))],
        "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{ruleId}\0{path}\0{line}"))),
        ruleId,
        Source: new FindingSource(FindingSourceKind.Deterministic, "roslyn", "Microsoft.CodeAnalysis", "5.6.0"));

    private sealed class FixedSensor(SensorScanResult result) : IDeterministicEvidenceSensor
    {
        public string Id => result.Provenance.SensorId;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
