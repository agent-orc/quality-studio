using System.Text.RegularExpressions;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>Keep the complete checkout-writing HTTP surface behind the handover planner.</summary>
[Trait("Category", "Guard")]
public sealed class RepositoryWriteRouteGuardTests
{
    [Fact]
    public void Every_repository_configuration_route_uses_the_handover_boundary()
    {
        var root = RepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "backend/QualityStudio.Api/Program.cs"));
        var rules = File.ReadAllText(Path.Combine(root, "backend/QualityStudio.Api/RulePoolEndpoints.cs"));
        // Review every mutating API handler when a route is added. A new checkout writer
        // cannot hide behind a new path that the repository-route checks below do not know.
        var safeHandlers = new HashSet<string>(StringComparer.Ordinal)
        {
            "CreateRepository", "ImportFromAgentStudio", "UpdateRepository", "ArchiveRepository",
            "EstimateReview", "StartReview", "Handover", "SensorScan", "GuidelineImpact",
            "RecordAttackJudgement", "PreviewScopeRule", "PauseReview", "ResumeReview",
            "CancelReview", "PinReviewRun", "UnpinReviewRun", "MutateThread", "MutateFindingState",
            "SignIn", "Revoke", "Review", "Ingest", "ValidateCustomRule",
        };
        var handoverHandlers = new HashSet<string>(StringComparer.Ordinal)
        {
            "CreateGuideline", "UpdateGuideline", "DeleteGuideline", "InstallGuideline",
            "AddScopeRule", "UpdateScopeRule", "DeleteScopeRule", "AddFindingSuppression",
            "AddScopedFindingSuppression", "DeleteFindingSuppression", "Import", "SetOverride",
            "RemoveOverride", "PutCustomRule", "DeleteCustomRule", "PutPack", "DeletePack",
            "SetApplicability", "ClearApplicability",
        };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "backend/QualityStudio.Api"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("MapMethods(", source, StringComparison.Ordinal);
            foreach (Match route in Regex.Matches(source,
                @"\bMap(?:Post|Put|Delete|Patch)\([^\n]*,\s*(?<handler>\w+)\)"))
            {
                var handler = route.Groups["handler"].Value;
                Assert.True(safeHandlers.Contains(handler) || handoverHandlers.Contains(handler),
                    $"Classify new mutating route {Path.GetFileName(file)}: {route.Value}");
            }
        }
        var programRoutes = Regex.Matches(program,
                "app\\.Map(?<method>Post|Put|Delete)\\(\\\"(?<path>/api/[^\\\"]+)\\\", (?<handler>\\w+)\\)")
            .Select(match => (Method: match.Groups["method"].Value, Path: match.Groups["path"].Value,
                Handler: match.Groups["handler"].Value))
            .Where(route => route.Path.Contains("/findings/suppressions", StringComparison.Ordinal)
                || route.Path.Contains("/guidelines", StringComparison.Ordinal) && !route.Path.EndsWith("/impact", StringComparison.Ordinal)
                || route.Path.Contains("/scope/rules", StringComparison.Ordinal) && !route.Path.EndsWith("/preview", StringComparison.Ordinal))
            .ToArray();
        string[] programSuffixes =
        [
            "Post /guidelines CreateGuideline", "Put /guidelines/{guidelineId} UpdateGuideline",
            "Delete /guidelines/{guidelineId} DeleteGuideline", "Post /guidelines/catalog/{catalogueId}/install InstallGuideline",
            "Post /scope/rules AddScopeRule", "Put /scope/rules/{index:int} UpdateScopeRule",
            "Delete /scope/rules/{index:int} DeleteScopeRule",
            "Post /findings/suppressions AddFindingSuppression",
            "Post /findings/suppressions/scoped AddScopedFindingSuppression",
            "Delete /findings/suppressions/{id} DeleteFindingSuppression",
        ];
        var expectedProgram = programSuffixes.SelectMany(suffix => new[] { "/api", "/api/repos/{repoId}" }
            .Select(prefix => suffix.Replace(" /", " " + prefix + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedProgram, programRoutes.Select(route => $"{route.Method} {route.Path} {route.Handler}")
            .Order(StringComparer.Ordinal));
        foreach (var handler in programRoutes.Select(route => route.Handler).Distinct(StringComparer.Ordinal))
        {
            var declaration = Regex.Match(program, @"static\s+(?:async\s+)?\S+\s+" + Regex.Escape(handler) + @"\([^)]*\)", RegexOptions.Singleline);
            Assert.True(declaration.Success, $"Missing handler declaration: {handler}");
            Assert.Contains("RepositoryWriteHandover handover", declaration.Value, StringComparison.Ordinal);
            var next = program.IndexOf("\nstatic ", declaration.Index + declaration.Length, StringComparison.Ordinal);
            var body = program[declaration.Index..(next < 0 ? program.Length : next)];
            Assert.Contains("handover.ExecuteAsync(", body, StringComparison.Ordinal);
        }

        var ruleRoutes = Regex.Matches(rules,
                """app\.Map(?<method>Post|Put|Delete)\(prefix \+ "(?<path>/rules/[^"]+)", (?<handler>\w+)\)""")
            .Where(match => !match.Groups["path"].Value.EndsWith("/validate", StringComparison.Ordinal))
            .Select(match => $"{match.Groups["method"].Value} {match.Groups["path"].Value} {match.Groups["handler"].Value}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "Delete /rules/applicability ClearApplicability", "Delete /rules/custom/{ruleId} DeleteCustomRule",
            "Delete /rules/overrides/{ruleId} RemoveOverride", "Delete /rules/packs/{packId} DeletePack",
            "Post /rules/import Import", "Put /rules/applicability SetApplicability",
            "Put /rules/custom/{ruleId} PutCustomRule", "Put /rules/overrides/{ruleId} SetOverride",
            "Put /rules/packs/{packId} PutPack",
        }, ruleRoutes);
        Assert.Contains("handover.ExecuteAsync", rules, StringComparison.Ordinal);
        foreach (var handler in ruleRoutes.Select(route => route.Split(' ')[2]).Distinct(StringComparer.Ordinal))
        {
            var declaration = Regex.Match(rules, @"private static\s+\S+\s+" + Regex.Escape(handler) + @"\([^)]*\)", RegexOptions.Singleline);
            Assert.True(declaration.Success, $"Missing rule handler declaration: {handler}");
            var next = rules.IndexOf("\n    private static ", declaration.Index + declaration.Length, StringComparison.Ordinal);
            var body = rules[declaration.Index..(next < 0 ? rules.Length : next)];
            Assert.True(body.Contains("Mutation(", StringComparison.Ordinal) || body.Contains("handover.ExecuteAsync(", StringComparison.Ordinal),
                $"Rule handler bypasses handover: {handler}");
        }
        Assert.Contains("suppressAudit", rules, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
