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
        // Any new unsafe API route requires an ownership review, even when it uses a new path family.
        var routeCounts = Directory.EnumerateFiles(Path.Combine(root, "backend/QualityStudio.Api"), "*.cs")
            .Select(path => (Name: Path.GetFileName(path), Count: Regex.Matches(File.ReadAllText(path),
                @"\.Map(Post|Put|Delete)\(").Count))
            .Where(item => item.Count > 0)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            ("ChangeReviewEndpoints.cs", 4), ("FederationEndpoints.cs", 2),
            ("Program.cs", 60), ("RulePoolEndpoints.cs", 10),
        }, routeCounts);
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
            var start = program.LastIndexOf("static ", program.IndexOf(handler + "(", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("RepositoryWriteHandover handover", program[start..(start + 400)], StringComparison.Ordinal);
        }
        Assert.Equal(10, programRoutes.Select(route => route.Handler).Distinct(StringComparer.Ordinal).Count());
        Assert.True(Regex.Matches(program, "handover\\.ExecuteAsync\\(").Count >= 10);

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
        Assert.Contains("suppressAudit", rules, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
