using System.Text.Json;
using Json.Schema;
using QualityStudio.Testing;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// The managed rule pool: custom rules and packs read at runtime, applicability replacing the
/// house-style default, layered overrides, and the validated, audited write path with rule-set
/// import and export. Every test uses its own repository and its own global rule folder, so none
/// of them touches the process-wide data root other tests resolve against.
/// </summary>
public sealed class RulePoolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-rule-pool-tests", Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(root, "repository");
    private string Global => Path.Combine(root, "global-rules");
    private string ProjectRules => Path.Combine(Repository, ".quality", "rules");

    public RulePoolTests()
    {
        Directory.CreateDirectory(Repository);
        Directory.CreateDirectory(Global);
    }

    [Fact]
    public void A_custom_repository_rule_is_enforced_without_a_rebuild()
    {
        Write(ProjectRules, "custom/ACME-CS-001-structured-logging.md", CustomRule());

        var catalogue = Resolver().Resolve(Repository);

        var rule = Assert.Single(catalogue.Rules, candidate => candidate.Rule.Id == "ACME-CS-001");
        Assert.Equal(RuleScopes.Project, rule.Origin);
        Assert.True(rule.EffectiveEnabled);
        Assert.Equal([RulePackRules.HouseStyleId], rule.SelectedBy);
        var input = Assert.Single(RuleCatalogueResolver.RenderAsReviewInputs(catalogue, "code", "dotnet"),
            candidate => candidate.Id == "ACME-CS-001");
        Assert.Contains("Log through the structured logger", input.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleCatalogueResolver.RenderAsReviewInputs(catalogue, "code", "angular"),
            candidate => candidate.Id == "ACME-CS-001");
        Assert.Contains(RuleScopes.Project, catalogue.SourceScopes);
    }

    [Fact]
    public void An_invalid_custom_rule_fails_reviews_closed_but_is_reported_by_inspection()
    {
        Write(ProjectRules, "custom/ACME-CS-001.md", CustomRule().Replace("## Detection", "## Notes", StringComparison.Ordinal));

        var exception = Assert.Throws<RuleConfigurationException>(() => Resolver().Resolve(Repository));
        var inspected = Resolver().Inspect(Repository);

        Assert.Contains("'## Detection'", exception.Message, StringComparison.Ordinal);
        Assert.False(inspected.IsValid);
        var diagnostic = Assert.Single(inspected.Diagnostics);
        Assert.Equal(".quality/rules/custom/ACME-CS-001.md", diagnostic.Source);
        Assert.Equal("ACME-CS-001", diagnostic.Subject);
        Assert.DoesNotContain(root, diagnostic.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(inspected.Rules, rule => rule.Rule.Id == "ACME-CS-001");
        Assert.Contains(inspected.Rules, rule => rule.Rule.Id == "QS-CS-003");
    }

    [Theory]
    [InlineData("QS-CS-900", "reserved for the built-in library")]
    [InlineData("ACME-NG-001", "must use the '-CS-' segment")]
    [InlineData("acme-cs-001", "must match <PREFIX>-<NG|CS|GN>-<NNN>")]
    public void A_custom_rule_id_must_not_collide_with_the_library_or_contradict_its_technology(string id, string message)
    {
        Write(ProjectRules, $"custom/{id}.md", CustomRule(id));

        var diagnostics = Resolver().Inspect(Repository).Diagnostics;

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_global_custom_rule_reaches_every_repository_and_a_project_cannot_redefine_it()
    {
        Write(Global, "custom/ACME-GN-001.md", CustomRule("ACME-GN-001", "generic"));

        var catalogue = Resolver().Resolve(Repository);
        Assert.Equal(RuleScopes.Global, Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "ACME-GN-001").Origin);

        Write(ProjectRules, "custom/ACME-GN-001.md", CustomRule("ACME-GN-001", "generic"));
        var diagnostic = Assert.Single(Resolver().Inspect(Repository).Diagnostics);
        Assert.Contains("already defined by the global rule pool", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_applicability_the_house_style_pack_reproduces_the_shipped_defaults()
    {
        var catalogue = Resolver().Resolve(Repository);

        Assert.Equal(RuleScopes.Default, catalogue.Applicability.Scope);
        Assert.Equal([RulePackRules.HouseStyleId], catalogue.Applicability.Packs);
        Assert.All(catalogue.Rules, rule => Assert.Equal(rule.Rule.DefaultOn, rule.EffectiveEnabled));
        Assert.Contains(catalogue.Packs, pack => pack.Pack.Id == "dotnet-service" && pack.Origin == RuleScopes.BuiltIn);
    }

    [Fact]
    public void Project_applicability_replaces_the_house_style_defaults()
    {
        Write(ProjectRules, "applicability.json", """
            { "schemaVersion": 1, "packs": ["angular-app", "public-website"], "reason": "Public Angular product site." }
            """);

        var catalogue = Resolver().Resolve(Repository);

        Assert.Equal(RuleScopes.Project, catalogue.Applicability.Scope);
        Assert.Equal(["angular-app", "public-website"], catalogue.Applicability.Packs);
        Assert.Equal("Public Angular product site.", catalogue.Applicability.Reason);
        Assert.All(catalogue.Rules.Where(rule => rule.Rule.Technology == "dotnet"), rule => Assert.False(rule.EffectiveEnabled));
        var seo = Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-GN-005");
        Assert.True(seo.EffectiveEnabled);
        Assert.Equal(["public-website"], seo.SelectedBy);
        Assert.True(Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-NG-001").EffectiveEnabled);
    }

    [Fact]
    public void Project_applicability_wins_over_global_and_an_empty_pack_list_leaves_only_overrides()
    {
        Write(Global, "applicability.json", """
            { "schemaVersion": 1, "packs": ["security-baseline"], "reason": "Host reviews security only." }
            """);
        var global = Resolver().Resolve(Repository);
        Assert.Equal(RuleScopes.Global, global.Applicability.Scope);
        Assert.False(Assert.Single(global.Rules, rule => rule.Rule.Id == "QS-CS-001").EffectiveEnabled);
        Assert.True(Assert.Single(global.Rules, rule => rule.Rule.Id == "QS-CS-005").EffectiveEnabled);

        Write(ProjectRules, "applicability.json", """
            { "schemaVersion": 1, "packs": [], "reason": "Only explicitly enabled rules apply here." }
            """);
        Write(ProjectRules, "overrides.json", """
            { "schemaVersion": 1, "overrides": [ { "id": "QS-CS-003", "enabled": true, "reason": "Cancellation matters here." } ] }
            """);
        var project = Resolver().Resolve(Repository);

        Assert.Equal(RuleScopes.Project, project.Applicability.Scope);
        Assert.Equal(["QS-CS-003"], project.Rules.Where(rule => rule.EffectiveEnabled).Select(rule => rule.Rule.Id));
    }

    [Fact]
    public void A_custom_pack_can_select_custom_rules_and_unknown_references_are_errors()
    {
        Write(ProjectRules, "custom/ACME-CS-001.md", CustomRule(defaultOn: false));
        Write(ProjectRules, "packs/acme-backend.json", """
            {
              "schemaVersion": 1, "id": "acme-backend", "version": "1.0.0", "title": "ACME backend",
              "description": "Our services.", "projectTypes": ["dotnet-api"],
              "include": [ { "technologies": ["dotnet"], "defaultOn": true }, { "ids": ["ACME-CS-001"] } ]
            }
            """);
        Write(ProjectRules, "applicability.json", """
            { "schemaVersion": 1, "packs": ["acme-backend"], "reason": "ACME service." }
            """);

        var catalogue = Resolver().Resolve(Repository);

        Assert.True(Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "ACME-CS-001").EffectiveEnabled);
        Assert.False(Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-GN-001").EffectiveEnabled);
        Assert.Equal(RuleScopes.Project, Assert.Single(catalogue.Packs, pack => pack.Pack.Id == "acme-backend").Origin);

        Write(ProjectRules, "packs/acme-backend.json", """
            {
              "schemaVersion": 1, "id": "acme-backend", "version": "1.0.1", "title": "ACME backend",
              "description": "Our services.", "projectTypes": [], "include": [ { "ids": ["ACME-CS-404"] } ]
            }
            """);
        var diagnostics = Resolver().Inspect(Repository).Diagnostics;
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("unknown rule id 'ACME-CS-404'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("unknown pack 'acme-backend'", StringComparison.Ordinal));
    }

    [Fact]
    public void Overrides_layer_global_then_project_by_rule_id()
    {
        Write(Global, "overrides.json", """
            { "schemaVersion": 1, "overrides": [
              { "id": "QS-CS-004", "enabled": false, "reason": "Host has no test projects." },
              { "id": "QS-CS-003", "severity": "critical", "reason": "Host-wide incident follow-up." } ] }
            """);
        Write(ProjectRules, "overrides.json", """
            { "schemaVersion": 1, "overrides": [ { "id": "QS-CS-004", "enabled": true, "reason": "This repository has tests." } ] }
            """);

        var catalogue = Resolver().Resolve(Repository);

        var tests = Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-CS-004");
        Assert.True(tests.EffectiveEnabled);
        Assert.Equal(RuleScopes.Project, tests.OverrideScope);
        var cancellation = Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-CS-003");
        Assert.Equal(FindingSeverity.Critical, cancellation.EffectiveSeverity);
        Assert.Equal(RuleScopes.Global, cancellation.OverrideScope);
        Assert.Equal(["built-in", "global", "project"], catalogue.SourceScopes);
    }

    [Fact]
    public void A_linked_override_file_fails_closed_even_after_a_valid_pool_was_cached()
    {
        Write(ProjectRules, "overrides.json", """
            { "schemaVersion": 1, "overrides": [ { "id": "QS-CS-004", "enabled": false, "reason": "No tests." } ] }
            """);
        var resolver = Resolver();
        Assert.False(Assert.Single(resolver.Resolve(Repository).Rules, rule => rule.Rule.Id == "QS-CS-004").EffectiveEnabled);
        var before = resolver.SourceState(Repository);
        var outside = Path.Combine(root, "outside-overrides.json");
        File.Move(Path.Combine(ProjectRules, "overrides.json"), outside);
        File.CreateSymbolicLink(Path.Combine(ProjectRules, "overrides.json"), outside);

        Assert.NotEqual(before, resolver.SourceState(Repository));
        Assert.Contains(resolver.Inspect(Repository).Diagnostics, diagnostic =>
            diagnostic.Source == ".quality/rules/overrides.json" && diagnostic.Message.Contains("symbolic link", StringComparison.Ordinal));
        Assert.Throws<RuleConfigurationException>(() => resolver.Resolve(Repository));
    }

    [Theory]
    [InlineData("custom", ".md", false)]
    [InlineData("custom", ".md", true)]
    [InlineData("packs", ".json", false)]
    [InlineData("packs", ".json", true)]
    public void Linked_rule_sources_fail_closed(string folder, string extension, bool linkFolder)
    {
        var target = Path.Combine(root, "outside-" + folder);
        Directory.CreateDirectory(target);
        var fileName = "linked" + extension;
        File.WriteAllText(Path.Combine(target, fileName), "{}");
        var sourceFolder = Path.Combine(ProjectRules, folder);
        Directory.CreateDirectory(ProjectRules);
        if (linkFolder)
            Directory.CreateSymbolicLink(sourceFolder, target);
        else
        {
            Directory.CreateDirectory(sourceFolder);
            File.CreateSymbolicLink(Path.Combine(sourceFolder, fileName), Path.Combine(target, fileName));
        }

        var source = ".quality/rules/" + folder + (linkFolder ? "/" : "/" + fileName);
        Assert.Contains(Resolver().Inspect(Repository).Diagnostics, diagnostic =>
            diagnostic.Source == source && diagnostic.Message.Contains("symbolic link", StringComparison.Ordinal));
        Assert.Throws<RuleConfigurationException>(() => Resolver().Resolve(Repository));
    }

    [Fact]
    public void The_source_state_changes_when_a_global_rule_file_changes()
    {
        var before = Resolver().SourceState(Repository);
        Write(Global, "custom/ACME-GN-001.md", CustomRule("ACME-GN-001", "generic"));

        Assert.NotEqual(before, Resolver().SourceState(Repository));
        Assert.Contains(Resolver().Resolve(Repository).Rules, rule => rule.Rule.Id == "ACME-GN-001");
    }

    [Fact]
    public void Setting_an_override_writes_the_project_file_and_an_audit_entry()
    {
        var store = Store();

        var catalogue = store.SetOverride(RuleScopes.Project,
            new RuleOverride("QS-CS-004", false, FindingSeverity.Low, "  No test project yet.  "), "operator");

        var rule = Assert.Single(catalogue.Rules, candidate => candidate.Rule.Id == "QS-CS-004");
        Assert.False(rule.EffectiveEnabled);
        Assert.Equal(FindingSeverity.Low, rule.EffectiveSeverity);
        var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRules, "overrides.json"))).RootElement;
        Assert.Equal(RuleOverrideDocument.SchemaId, file.GetProperty("$schema").GetString());
        var written = Assert.Single(file.GetProperty("overrides").EnumerateArray());
        Assert.Equal("low", written.GetProperty("severity").GetString());
        Assert.Equal("No test project yet.", written.GetProperty("reason").GetString());
        var entry = Assert.Single(store.ReadAudit(RuleScopes.Project));
        Assert.Equal(("operator", "override.set", "QS-CS-004", "No test project yet."), (entry.Actor, entry.Action, entry.Target, entry.Reason));
        Assert.Null(entry.Before);
        Assert.False(entry.After!["enabled"]!.GetValue<bool>());
        Assert.True(File.Exists(QualityDataRoot.Combine(Repository, "rules", RulePoolStore.AuditFileName)));
        Assert.False(File.Exists(Path.Combine(ProjectRules, RulePoolStore.AuditFileName)),
            "the audit trail is generated evidence and belongs in the data root, not the checkout");

        store.RemoveOverride(RuleScopes.Project, "QS-CS-004", "Tests were added.", "operator");

        Assert.False(File.Exists(Path.Combine(ProjectRules, "overrides.json")));
        var removal = store.ReadAudit(RuleScopes.Project)[0];
        Assert.Equal("override.remove", removal.Action);
        Assert.NotNull(removal.Before);
        Assert.Null(removal.After);
    }

    [Fact]
    public void A_change_that_would_break_the_pool_is_rejected_before_anything_is_written()
    {
        var store = Store();

        var unknown = Assert.Throws<RulePoolValidationException>(() => store.SetOverride(RuleScopes.Project,
            new RuleOverride("QS-CS-999", false, null, "Typo."), "operator"));
        var noReason = Assert.Throws<RulePoolValidationException>(() => store.SetOverride(RuleScopes.Project,
            new RuleOverride("QS-CS-003", false, null, " "), "operator"));
        var unknownPack = Assert.Throws<RulePoolValidationException>(() => store.SetApplicability(RuleScopes.Project,
            ["no-such-pack"], "Try it.", "operator"));

        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown rule id 'QS-CS-999'", StringComparison.Ordinal));
        Assert.Contains("reason is required", noReason.Message, StringComparison.Ordinal);
        Assert.Contains(unknownPack.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown pack 'no-such-pack'", StringComparison.Ordinal));
        Assert.False(Directory.Exists(ProjectRules));
        Assert.Empty(store.ReadAudit(RuleScopes.Project));
    }

    [Fact]
    public void A_broken_hand_edit_can_still_be_repaired_through_the_store()
    {
        Write(ProjectRules, "overrides.json", """
            { "schemaVersion": 1, "overrides": [
              { "id": "ACME-CS-001", "enabled": false, "reason": "Its custom rule was deleted by hand." },
              { "id": "QS-CS-004", "enabled": false, "reason": "No tests." } ] }
            """);
        var store = Store();
        Assert.False(store.Inspect().IsValid);

        var catalogue = store.RemoveOverride(RuleScopes.Project, "ACME-CS-001", "Rule no longer exists.", "operator");

        Assert.True(catalogue.IsValid);
        Assert.False(Assert.Single(catalogue.Rules, rule => rule.Rule.Id == "QS-CS-004").EffectiveEnabled);
    }

    [Fact]
    public void Custom_rules_are_created_validated_and_deleted_with_their_overrides()
    {
        var store = Store();
        var invalid = Assert.Throws<RulePoolValidationException>(() => store.PutCustomRule(RuleScopes.Project, "ACME-CS-001",
            CustomRule().Replace("severity: medium", "severity: urgent", StringComparison.Ordinal), "Adopt logging rule.", "operator"));
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Message.Contains("severity 'urgent'", StringComparison.Ordinal));
        var mismatch = Assert.Throws<RulePoolValidationException>(() => store.PutCustomRule(RuleScopes.Project, "ACME-CS-002",
            CustomRule(), "Wrong route.", "operator"));
        Assert.Contains("file name must start with the rule id", string.Join(" ", mismatch.Diagnostics), StringComparison.Ordinal);

        store.PutCustomRule(RuleScopes.Project, "ACME-CS-001", CustomRule().Replace("\n", "\r\n", StringComparison.Ordinal),
            "Adopt logging rule.", "operator");
        store.SetOverride(RuleScopes.Project, new RuleOverride("ACME-CS-001", null, FindingSeverity.High, "Logging incidents."), "operator");
        var path = Path.Combine(ProjectRules, "custom", "ACME-CS-001.md");
        Assert.DoesNotContain("\r\n", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(FindingSeverity.High,
            Assert.Single(Resolver().Resolve(Repository).Rules, rule => rule.Rule.Id == "ACME-CS-001").EffectiveSeverity);

        var updated = store.PutCustomRule(RuleScopes.Project, "ACME-CS-001",
            CustomRule(version: "1.1.0"), "Clarified the statement.", "operator");
        Assert.Equal("1.1.0", Assert.Single(updated.Rules, rule => rule.Rule.Id == "ACME-CS-001").Rule.Version);

        var catalogue = store.DeleteCustomRule(RuleScopes.Project, "ACME-CS-001", "Superseded by an analyzer.", "operator");

        Assert.True(catalogue.IsValid);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(ProjectRules, "overrides.json")));
        Assert.Equal(["custom-rule.delete", "custom-rule.put", "override.set", "custom-rule.put"],
            store.ReadAudit(RuleScopes.Project).Select(entry => entry.Action));
        Assert.Throws<RulePoolEntryNotFoundException>(() =>
            store.DeleteCustomRule(RuleScopes.Project, "ACME-CS-001", "Again.", "operator"));
    }

    [Fact]
    public void A_dry_run_validates_a_custom_rule_in_the_context_of_the_pool_without_writing()
    {
        Write(Global, "custom/ACME-CS-001.md", CustomRule());
        var store = Store();

        var collision = Assert.Throws<RulePoolValidationException>(() => store.PutCustomRule(RuleScopes.Project,
            "ACME-CS-001", CustomRule(), string.Empty, "operator", dryRun: true));
        var catalogue = store.PutCustomRule(RuleScopes.Project, "ACME-CS-002", CustomRule("ACME-CS-002"), string.Empty,
            "operator", dryRun: true);

        Assert.Contains("already defined by the global rule pool", string.Join(" ", collision.Diagnostics), StringComparison.Ordinal);
        Assert.Contains(catalogue.Rules, rule => rule.Rule.Id == "ACME-CS-002");
        Assert.False(Directory.Exists(ProjectRules));
        Assert.Empty(store.ReadAudit(RuleScopes.Project));
    }

    [Fact]
    public void Global_changes_are_written_to_the_global_folder_and_its_own_audit_trail()
    {
        var store = Store();

        store.SetApplicability(RuleScopes.Global, ["dotnet-service"], "Host serves .NET APIs only.", "admin");

        Assert.True(File.Exists(Path.Combine(Global, "applicability.json")));
        Assert.Equal("admin", Assert.Single(store.ReadAudit(RuleScopes.Global)).Actor);
        Assert.Empty(store.ReadAudit(RuleScopes.Project));
        Assert.Equal(RuleScopes.Global, Resolver().Resolve(Repository).Applicability.Scope);
        store.ClearApplicability(RuleScopes.Global, "Back to the house style.", "admin");
        Assert.Equal(RuleScopes.Default, Resolver().Resolve(Repository).Applicability.Scope);
    }

    [Fact]
    public void Global_custom_rule_write_cannot_invalidate_another_registered_repository()
    {
        var other = Path.Combine(root, "other");
        Write(Path.Combine(other, ".quality", "rules"), "custom/ACME-CS-001.md", CustomRule());
        var store = new RulePoolStore(Repository, globalRulesDirectory: Global,
            repositories: [new RulePoolRepository("other", other)]);

        var rejected = Assert.Throws<RulePoolValidationException>(() => store.PutCustomRule(RuleScopes.Global,
            "ACME-CS-001", CustomRule(), "Share the rule.", "admin"));

        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Message.Contains("already defined", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Global, "custom", "ACME-CS-001.md")));
        Assert.Empty(store.ReadAudit(RuleScopes.Global));
        Assert.True(new RuleCatalogueResolver(Global).Resolve(other).IsValid);
    }

    [Fact]
    public void Global_import_preview_and_apply_reject_a_collision_in_another_registered_repository()
    {
        var other = Path.Combine(root, "other");
        Write(Path.Combine(other, ".quality", "rules"), "custom/ACME-CS-001.md", CustomRule());
        var store = new RulePoolStore(Repository, globalRulesDirectory: Global,
            repositories: [new RulePoolRepository("other", other)]);
        var rule = RuleMarkdown.ParseCustom(CustomRule(), "ACME-CS-001.md", new List<string>())!;
        var set = new RuleSetDocument(RuleSetDocument.SchemaId, 1, "collision", null, null, null, null,
            null, [], [rule], []);

        var preview = store.Import(RuleScopes.Global, set, "merge", dryRun: true, "", "admin");
        var rejected = Assert.Throws<RulePoolValidationException>(() =>
            store.Import(RuleScopes.Global, set, "merge", dryRun: false, "Share the rule.", "admin"));

        Assert.False(preview.Valid);
        Assert.Contains(preview.Diagnostics, diagnostic => diagnostic.Message.Contains("already defined", StringComparison.Ordinal));
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Message.Contains("already defined", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Global, "custom", "ACME-CS-001.md")));
        Assert.Empty(store.ReadAudit(RuleScopes.Global));
    }

    [Fact]
    public void An_exported_rule_set_round_trips_into_another_repository()
    {
        var store = Store();
        store.PutCustomRule(RuleScopes.Project, "ACME-CS-001", CustomRule(defaultOn: false), "Adopt logging rule.", "operator");
        store.PutPack(RuleScopes.Project, "acme-backend", new RulePackDocument(null, 1, "acme-backend", "1.0.0", "ACME backend",
            "Our services.", ["dotnet-api"], [new RulePackSelector(Technologies: ["dotnet", "generic"], DefaultOn: true),
                new RulePackSelector(Ids: ["ACME-CS-001"])]), "Define our pack.", "operator");
        store.SetApplicability(RuleScopes.Project, ["acme-backend"], "ACME service.", "operator");
        store.SetOverride(RuleScopes.Project, new RuleOverride("QS-CS-004", null, FindingSeverity.Info, "Table tests."), "operator");

        var exported = store.Export(RuleScopes.Project, "acme-service");
        var json = JsonSerializer.Serialize(exported, AttackCoverageJson.Options);
        AssertRuleSetSchemaValid(json);

        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        var otherStore = new RulePoolStore(other, globalRulesDirectory: Global);
        var parsed = RulePoolStore.ParseRuleSet(JsonDocument.Parse(json).RootElement);
        var preview = otherStore.Import(RuleScopes.Project, parsed, "replace", dryRun: true, string.Empty, "operator");
        Assert.True(preview.Valid);
        Assert.False(preview.Applied);
        Assert.False(preview.ModifiedSinceExport);
        Assert.Contains(new RuleImportChange("custom-rule", "ACME-CS-001", "added"), preview.Changes);
        Assert.False(Directory.Exists(Path.Combine(other, ".quality")));

        var imported = otherStore.Import(RuleScopes.Project, parsed, "replace", dryRun: false, "Adopt ACME rules.", "operator");

        Assert.True(imported.Applied);
        var source = Resolver().Resolve(Repository);
        var target = new RuleCatalogueResolver(Global).Resolve(other);
        Assert.Equal(Effective(source), Effective(target));
        Assert.Equal(exported.Digest, otherStore.Export(RuleScopes.Project, "acme-service").Digest);
        var audit = Assert.Single(otherStore.ReadAudit(RuleScopes.Project));
        Assert.Equal(("rule-set.import", "acme-service"), (audit.Action, audit.Target));
        Assert.Equal("replace", audit.After!["mode"]!.GetValue<string>());
    }

    [Fact]
    public void Import_merge_keeps_existing_entries_replace_removes_them_and_edits_are_reported()
    {
        var store = Store();
        store.PutCustomRule(RuleScopes.Project, "ACME-CS-001", CustomRule(), "Existing rule.", "operator");
        store.SetOverride(RuleScopes.Project, new RuleOverride("QS-CS-004", false, null, "No tests."), "operator");
        var set = new RuleSetDocument(RuleSetDocument.SchemaId, 1, "incoming", "1.5.0", null, "project",
            "sha256:" + new string('0', 64), null,
            [new RuleOverride("QS-CS-003", null, FindingSeverity.Critical, "Outage follow-up.")],
            [RuleMarkdown.ParseCustom(CustomRule("ACME-CS-002"), "ACME-CS-002.md", new List<string>())!], []);

        var merged = store.Import(RuleScopes.Project, set, "merge", dryRun: false, "Merge team rules.", "operator");

        Assert.True(merged.ModifiedSinceExport);
        Assert.Contains(merged.Catalogue.Rules, rule => rule.Rule.Id == "ACME-CS-001");
        Assert.Contains(merged.Catalogue.Rules, rule => rule.Rule.Id == "ACME-CS-002");
        Assert.False(Assert.Single(merged.Catalogue.Rules, rule => rule.Rule.Id == "QS-CS-004").EffectiveEnabled);

        var replaced = store.Import(RuleScopes.Project, set, "replace", dryRun: false, "Adopt the team set exactly.", "operator");

        Assert.DoesNotContain(replaced.Catalogue.Rules, rule => rule.Rule.Id == "ACME-CS-001");
        Assert.True(Assert.Single(replaced.Catalogue.Rules, rule => rule.Rule.Id == "QS-CS-004").EffectiveEnabled);
        Assert.Contains(new RuleImportChange("custom-rule", "ACME-CS-001", "removed"), replaced.Changes);
        Assert.Contains(new RuleImportChange("override", "QS-CS-004", "removed"), replaced.Changes);
        Assert.False(File.Exists(Path.Combine(ProjectRules, "custom", "ACME-CS-001.md")));
    }

    [Fact]
    public void Replace_import_with_empty_overrides_repairs_an_unreadable_override_file()
    {
        var path = Path.Combine(ProjectRules, "overrides.json");
        Write(ProjectRules, "overrides.json", "{ invalid json");
        var store = Store();
        var empty = new RuleSetDocument(RuleSetDocument.SchemaId, 1, "empty", null, null, null, null,
            null, [], [], []);
        Assert.False(store.Inspect().IsValid);

        var preview = store.Import(RuleScopes.Project, empty, "replace", dryRun: true, "", "operator");
        Assert.True(preview.Valid);
        Assert.False(preview.Applied);
        Assert.True(File.Exists(path));

        var imported = store.Import(RuleScopes.Project, empty, "replace", dryRun: false, "Repair overrides.", "operator");

        Assert.True(imported.Applied);
        Assert.False(File.Exists(path));
        Assert.True(store.Inspect().IsValid);
        Assert.Equal("rule-set.import", Assert.Single(store.ReadAudit(RuleScopes.Project)).Action);
    }

    [Fact]
    public void An_invalid_rule_set_is_reported_by_a_dry_run_and_rejected_on_apply()
    {
        var store = Store();
        var set = new RuleSetDocument(null, 1, "broken", null, null, null, null,
            new RuleSetApplicability(["missing-pack"], "Try."),
            [new RuleOverride("QS-CS-999", false, null, "Typo.")], [], []);

        var preview = store.Import(RuleScopes.Project, set, "merge", dryRun: true, string.Empty, "operator");
        var rejected = Assert.Throws<RulePoolValidationException>(() =>
            store.Import(RuleScopes.Project, set, "merge", dryRun: false, "Apply.", "operator"));

        Assert.False(preview.Valid);
        Assert.Contains(preview.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown pack 'missing-pack'", StringComparison.Ordinal));
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown rule id 'QS-CS-999'", StringComparison.Ordinal));
        Assert.False(Directory.Exists(ProjectRules));
        Assert.Throws<RulePoolValidationException>(() => RulePoolStore.ParseRuleSet(
            JsonDocument.Parse("""{ "schemaVersion": 1, "overrides": [], "customRules": [], "packs": [], "extra": true }""").RootElement));
    }

    [Fact]
    public void Every_built_in_rule_file_parses_to_its_catalogue_entry_with_the_runtime_parser()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var catalogue = RuleCatalogueResolver.BuiltInCatalogue.Entries.ToDictionary(entry => entry.Id);
        var files = Directory.EnumerateFiles(Path.Combine(repository, "rules"), "QS-*.md", SearchOption.AllDirectories).ToArray();
        Assert.Equal(catalogue.Count, files.Length);
        foreach (var file in files)
        {
            // The runtime parser reserves QS- for the library, so read each authored file under a
            // custom prefix; everything else must come out exactly as the generator wrote it.
            var text = File.ReadAllText(file);
            var id = RuleCatalogueResolver.PeekId(text)!;
            var custom = "QX" + id[2..];
            var errors = new List<string>();
            var parsed = RuleMarkdown.ParseCustom(text.Replace("id: " + id, "id: " + custom, StringComparison.Ordinal),
                custom + ".md", errors);
            Assert.True(parsed is not null, $"{Path.GetFileName(file)}: {string.Join(" ", errors)}");
            AssertSameRule(catalogue[id], parsed! with { Id = id });

            var rendered = RuleMarkdown.ParseCustom(RuleMarkdown.Render(parsed), custom + ".md", errors);
            Assert.True(rendered is not null, string.Join(" ", errors));
            AssertSameRule(parsed, rendered!);
        }
    }

    [Fact]
    public void The_built_in_packs_and_written_documents_conform_to_their_schemas()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        using var packs = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "backend",
            "AgentOrchestrator.CodeQuality", "catalogues", "rule-packs.v1.json")));
        AssertSchemaValid("rule-pack-catalogue.v1.schema.json", packs.RootElement);

        var store = Store();
        store.SetApplicability(RuleScopes.Project, ["dotnet-service"], "A service.", "operator");
        store.PutPack(RuleScopes.Project, "acme", new RulePackDocument(null, 1, "acme", "1.0.0", "ACME", "Ours.", [],
            [new RulePackSelector(Kinds: ["security"])]), "Our pack.", "operator");
        using var applicability = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRules, "applicability.json")));
        AssertSchemaValid("rule-applicability.v1.schema.json", applicability.RootElement);
        using var pack = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRules, "packs", "acme.json")));
        AssertSchemaValid("rule-pack.v1.schema.json", pack.RootElement);
        foreach (var line in File.ReadLines(store.AuditPath(RuleScopes.Project)))
        {
            using var entry = JsonDocument.Parse(line);
            AssertSchemaValid("rule-audit.v1.schema.json", entry.RootElement);
        }
    }

    private static IEnumerable<string> Effective(ResolvedRuleCatalogue catalogue) => catalogue.Rules.Select(rule =>
        $"{rule.Rule.Id}|{rule.EffectiveEnabled}|{rule.EffectiveSeverity}|{rule.Rule.Version}|{string.Join(',', rule.SelectedBy ?? [])}");

    private static void AssertSameRule(RuleDefinition expected, RuleDefinition actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected with { Kinds = [], DeterministicRuleIds = [], ChangeHistory = [] }),
            JsonSerializer.Serialize(actual with { Kinds = [], DeterministicRuleIds = [], ChangeHistory = [] }));
        Assert.Equal(expected.Kinds, actual.Kinds);
        Assert.Equal(expected.DeterministicRuleIds, actual.DeterministicRuleIds);
        Assert.Equal(expected.ChangeHistory, actual.ChangeHistory);
    }

    private static void AssertRuleSetSchemaValid(string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertSchemaValid("rule-set.v1.schema.json", document.RootElement);
    }

    private static void AssertSchemaValid(string schemaFile, JsonElement element)
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        foreach (var dependency in new[] { "rule-config.v1.schema.json", "rule-catalogue.v1.schema.json", "rule-pack.v1.schema.json" }
                     .Where(name => name != schemaFile))
            _ = JsonSchema.FromText(File.ReadAllText(Path.Combine(repository, "schemas", dependency)), options);
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(repository, "schemas", schemaFile)), options);
        var result = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private RuleCatalogueResolver Resolver() => new(Global);

    private RulePoolStore Store() => new(Repository, globalRulesDirectory: Global);

    private static void Write(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static string CustomRule(string id = "ACME-CS-001", string technology = "dotnet", bool defaultOn = true,
        string version = "1.0.0") => $$"""
        ---
        id: {{id}}
        version: {{version}}
        title: Log through the structured logger
        technology: {{technology}}
        kinds: [code]
        category: logging
        severity: medium
        defaultOn: {{(defaultOn ? "true" : "false")}}
        autofixable: false
        deterministicRuleIds: [CA2254]
        since: 1.0.0
        ---

        ## Statement

        Write diagnostics through ILogger with message templates, never through Console.

        ## Rationale

        Console output never reaches the host's log pipeline, so an incident has no trail.

        ## Detection

        Look for Console.Write and string interpolation inside logger calls. Test output helpers do not count.

        ## Good example

        ```csharp
        logger.LogInformation("Review {RunId} started", runId);
        ```

        ## Bad example

        ```csharp
        Console.WriteLine($"Review {runId} started");
        ```

        ## Change history

        {{(version == "1.0.0" ? "" : $"- {version} (2026-09-28): Clarified the statement.\n")}}- 1.0.0 (2026-09-28): Initial rule.
        """;

    public void Dispose()
    {
        try { TemporaryDirectory.Delete(root); } catch (IOException) { }
    }
}
