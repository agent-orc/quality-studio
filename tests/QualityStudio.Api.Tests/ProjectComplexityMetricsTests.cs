using System.Diagnostics;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Api;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Covers dossier slice S4: complexity as a thresholded metric feeding the project dashboard.
/// </summary>
public sealed class ProjectComplexityMetricsTests
{
    [Fact]
    public void Cyclomatic_complexity_counts_each_decision_point_once()
    {
        const string source = """
            public sealed class Sample
            {
                public int Rank(int value, string? label)
                {
                    if (value > 10 && value < 20) return 1;
                    foreach (var character in label ?? string.Empty)
                    {
                        if (character == 'x' || character == 'y') return 2;
                    }
                    return value > 0 ? 3 : 4;
                }
            }
            """;

        var member = Assert.Single(CSharpComplexityScanner.Scan(source));

        Assert.Equal("Sample.Rank(int, string?)", member.Symbol);
        Assert.Equal(3, member.Line);
        // 1 + if + && + foreach + ?? + if + || + ternary
        Assert.Equal(8, member.Complexity);
    }

    [Fact]
    public void Loops_switches_and_catch_filters_are_decision_points()
    {
        const string source = """
            public sealed class Branchy
            {
                public int Run(int value)
                {
                    do { value--; } while (value > 0);
                    for (var i = 0; i < 3; i++) value++;
                    switch (value)
                    {
                        case 1: return 1;
                        case 2: return 2;
                        default: break;
                    }
                    try { value++; }
                    catch (System.IO.IOException exception) when (exception.HResult != 0) { }
                    return value switch { 1 => 10, 2 => 20, _ => 30 };
                }
            }
            """;

        var member = Assert.Single(CSharpComplexityScanner.Scan(source));

        // 1 + do + for + 2 cases + catch + when + 2 non-discard arms; "default" and "_" add nothing.
        Assert.Equal(9, member.Complexity);
    }

    [Fact]
    public void Decision_keywords_inside_comments_and_literals_are_ignored()
    {
        const string source = """"
            public sealed class Quiet
            {
                public int Read()
                {
                    // if (a && b) return 1;
                    var text = "if (x || y) { }";
                    var verbatim = @"foreach (var z in list) { ""quoted"" }";
                    var raw = """
                        while (true) { if (x) { } }
                        """;
                    /* while (true) { if (x) { } } */
                    return text.Length + verbatim.Length + raw.Length;
                }
            }
            """";

        var member = Assert.Single(CSharpComplexityScanner.Scan(source));

        Assert.Equal("Quiet.Read()", member.Symbol);
        Assert.Equal(1, member.Complexity);
    }

    [Fact]
    public void Branches_interpolated_into_a_string_are_still_counted()
    {
        const string source = """"
            public sealed class Interpolating
            {
                public string Describe(int value)
                {
                    return $"v={(value > 0 ? "p" : "n")} w={(value > 1 && value < 9 ? 1 : 2)}";
                }
            }
            """";

        // 1 + two ternaries + one "&&": the expressions are real code, not literal text.
        Assert.Equal(4, Assert.Single(CSharpComplexityScanner.Scan(source)).Complexity);
    }

    [Fact]
    public void Nullable_annotations_and_null_conditional_access_are_not_ternaries()
    {
        const string source = """
            public sealed class Nullables
            {
                public int Count(int? first, string? second)
                {
                    int? local = first;
                    string? label = second;
                    System.Collections.Generic.List<int?> values = [];
                    var city = second
                        ?.Trim()
                        ?.ToUpperInvariant();
                    return local ?? values.Count + label!.Length + (city?.Length ?? 0);
                }
            }
            """;

        var member = Assert.Single(CSharpComplexityScanner.Scan(source));

        // Only the two "??" count. Annotations and "?." chains are not branches, and the result does
        // not depend on how the chain is wrapped across lines.
        Assert.Equal(3, member.Complexity);
    }

    [Fact]
    public void A_ternary_is_counted_regardless_of_surrounding_whitespace()
    {
        var spaced = Assert.Single(CSharpComplexityScanner.Scan(
            "public class S { public int M(int v) { return v > 0 ? 1 : 2; } }"));
        var tight = Assert.Single(CSharpComplexityScanner.Scan(
            "public class S { public int M(int v) { return v>0?1:2; } }"));

        Assert.Equal(2, spaced.Complexity);
        Assert.Equal(tight.Complexity, spaced.Complexity);
    }

    [Fact]
    public void Local_functions_fold_into_the_declaring_member()
    {
        const string source = """
            public sealed class Nested
            {
                public int Outer(int value)
                {
                    int Inner(int inner) => inner > 0 ? 1 : 2;
                    return value > 0 ? Inner(value) : 0;
                }
            }
            """;

        var member = Assert.Single(CSharpComplexityScanner.Scan(source));

        Assert.Equal("Nested.Outer(int)", member.Symbol);
        Assert.Equal(3, member.Complexity);
    }

    [Fact]
    public void Declaration_shapes_that_defeat_text_matching_are_measured()
    {
        const string source = """
            public sealed class Awkward
            {
                public (int Start, int End)? Locate(string value) =>
                    value.Length > 0 ? (0, value.Length) : null;

                public async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<int>> LoadAsync(int id)
                {
                    await System.Threading.Tasks.Task.Yield();
                    return id > 0 ? [id] : [];
                }

                public static Awkward operator +(Awkward left, Awkward right) =>
                    left is null || right is null ? null! : left;
            }

            public readonly ref struct Cursor
            {
                public int Advance(int by) => by > 0 ? by : 0;
            }
            """;

        var members = CSharpComplexityScanner.Scan(source);

        // A tuple return, a nested generic return, an operator, and a ref struct member: all four are
        // ordinary syntax nodes, so none is dropped and none borrows another declaration's name.
        Assert.Equal(
            [
                "Awkward.Locate(string)",
                "Awkward.LoadAsync(int)",
                "Awkward.operator +(Awkward, Awkward)",
                "Cursor.Advance(int)",
            ],
            members.Select(member => member.Symbol));
        Assert.Equal(2, Assert.Single(members, m => m.Symbol.StartsWith("Awkward.Locate", StringComparison.Ordinal)).Complexity);
        Assert.Equal(3, Assert.Single(members, m => m.Symbol.StartsWith("Awkward.operator", StringComparison.Ordinal)).Complexity);
    }

    [Fact]
    public void Overloads_are_distinct_symbols_with_distinct_fingerprints()
    {
        const string source = """
            public sealed class Sender
            {
                public int Send(int value) => value > 0 ? 1 : 2;

                public int Send(string value) => value.Length > 0 ? 1 : 2;
            }
            """;

        var members = CSharpComplexityScanner.Scan(source);

        Assert.Equal(["Sender.Send(int)", "Sender.Send(string)"], members.Select(member => member.Symbol));
        Assert.NotEqual(
            CSharpComplexityScanner.Fingerprint("src/A.cs", members[0].Symbol),
            CSharpComplexityScanner.Fingerprint("src/A.cs", members[1].Symbol));
    }

    [Fact]
    public void Expression_bodied_and_constructor_members_are_measured()
    {
        const string source = """
            public sealed class Shapes
            {
                public Shapes(int value)
                {
                    Value = value > 0 ? value : 0;
                }

                public int Value { get; }

                public int Scale(int factor) => factor > 0 && Value > 0 ? Value * factor : 0;
            }
            """;

        var members = CSharpComplexityScanner.Scan(source);

        Assert.Equal(2, members.Count);
        Assert.Equal(2, Assert.Single(members, member => member.Symbol == "Shapes.Shapes(int)").Complexity);
        // 1 + "&&" + ternary
        Assert.Equal(3, Assert.Single(members, member => member.Symbol == "Shapes.Scale(int)").Complexity);
    }

    [Fact]
    public void Regions_excluded_by_conditional_compilation_are_not_measured()
    {
        const string source = """
            public sealed class Flags
            {
                public int Pick(int value)
                {
            #if DEBUG
                    if (value > 0) return 1;
            #endif
                    return 0;
                }
            }
            """;

        // DEBUG is not defined for this parse, so the guarded branch is inactive text, not code.
        Assert.Equal(1, Assert.Single(CSharpComplexityScanner.Scan(source)).Complexity);
    }

    [Fact]
    public void A_record_primary_constructor_does_not_swallow_the_record_body()
    {
        const string source = """
            public sealed record Money(decimal Amount, string Currency)
            {
                public int Rank() => Amount > 0 ? 1 : 2;

                public bool Valid(string? code) => code is not null && code.Length == 3;
            }
            """;

        var members = CSharpComplexityScanner.Scan(source);

        Assert.Equal(["Money.Rank()", "Money.Valid(string?)"], members.Select(member => member.Symbol));
        Assert.All(members, member => Assert.Equal(2, member.Complexity));
    }

    [Fact]
    public void Members_are_attributed_to_their_declaring_type_including_nesting()
    {
        const string source = """
            public sealed class First
            {
                public int One(int value) => value;

                public sealed class Inner
                {
                    public int Deep(int value) => value;
                }
            }

            public sealed class Second
            {
                public int Two(int value) => value;
            }
            """;

        Assert.Equal(
            ["First.One(int)", "First.Inner.Deep(int)", "Second.Two(int)"],
            CSharpComplexityScanner.Scan(source).Select(member => member.Symbol));
    }

    [Fact]
    public void The_declaration_line_skips_attribute_lists()
    {
        const string source = """
            public sealed class Marked
            {
                [System.Obsolete]
                public int Value(int input) => input;
            }
            """;

        Assert.Equal(4, Assert.Single(CSharpComplexityScanner.Scan(source)).Line);
    }

    [Theory]
    [InlineData("src/Widget.cs", "public class W { }", ComplexityFileKind.Source)]
    [InlineData("src/Widget.g.cs", "public class W { }", ComplexityFileKind.Generated)]
    [InlineData("src/Widget.Designer.cs", "public class W { }", ComplexityFileKind.Generated)]
    [InlineData("src/Widget.cs", "// <auto-generated />\npublic class W { }", ComplexityFileKind.Generated)]
    [InlineData("tests/WidgetTests.cs", "public class W { }", ComplexityFileKind.Test)]
    [InlineData("tests/Helper.cs", "public class W { }", ComplexityFileKind.Test)]
    [InlineData("tests\\Helper.cs", "public class W { }", ComplexityFileKind.Test)]
    public void Files_are_classified_for_hold_out(string path, string text, ComplexityFileKind expected) =>
        Assert.Equal(expected, CSharpComplexityScanner.Classify(path, text));

    [Fact]
    public void Held_out_sources_enter_the_projection_only_when_explicitly_included()
    {
        var files = new[]
        {
            ("src/Real.cs", ComplexClass("Real", "Decide", 4)),
            ("tests/RealTests.cs", ComplexClass("RealTests", "Decide", 4)),
        };

        // The same composition the dashboard performs: classify, hold out, scan, aggregate.
        ProjectComplexityMetricsResponse Project(ComplexityAnalysisOptions options)
        {
            var included = files
                .Where(file => options.Includes(CSharpComplexityScanner.Classify(file.Item1, file.Item2)))
                .Select(file => (file.Item1, (IReadOnlyList<MemberComplexity>)CSharpComplexityScanner.Scan(file.Item2)))
                .ToArray();
            return CSharpComplexityScanner.Aggregate(
                included, files.Length - included.Length, skippedFiles: 0, options);
        }

        var held = Project(ComplexityAnalysisOptions.Default);
        Assert.Equal(1, held.AnalyzedFiles);
        Assert.Equal(1, held.ExcludedFiles);
        Assert.Equal(1, held.AnalyzedSymbols);

        var included = Project(new ComplexityAnalysisOptions(IncludeTests: true));
        Assert.Equal(2, included.AnalyzedFiles);
        Assert.Equal(0, included.ExcludedFiles);
        Assert.Equal(2, included.AnalyzedSymbols);
    }

    [Fact]
    public void The_configuration_hash_distinguishes_every_knob()
    {
        var defaults = ComplexityAnalysisOptions.Default;

        Assert.NotEqual(defaults.ConfigHash, new ComplexityAnalysisOptions(IncludeGenerated: true).ConfigHash);
        Assert.NotEqual(defaults.ConfigHash, new ComplexityAnalysisOptions(IncludeTests: true).ConfigHash);
        Assert.NotEqual(defaults.ConfigHash, new ComplexityAnalysisOptions(Threshold: 30).ConfigHash);
        Assert.Equal(defaults.ConfigHash, ComplexityAnalysisOptions.Default.ConfigHash);
    }

    [Fact]
    public void A_threshold_below_the_last_fixed_bucket_edge_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComplexityAnalysisOptions(Threshold: 15));

    [Fact]
    public void Aggregate_buckets_symbols_and_ranks_breaches_by_complexity()
    {
        var analyzed = new (string, IReadOnlyList<MemberComplexity>)[]
        {
            ("src/A.cs", [new MemberComplexity("A.Small()", 4, 3), new MemberComplexity("A.Huge()", 20, 40)]),
            ("src/B.cs", [new MemberComplexity("B.Medium()", 8, 12), new MemberComplexity("B.Big()", 30, 26)]),
        };

        var result = CSharpComplexityScanner.Aggregate(
            analyzed, excludedFiles: 5, skippedFiles: 2, ComplexityAnalysisOptions.Default);

        Assert.Equal("CA1502", result.Rule);
        Assert.Equal(25, result.Threshold);
        Assert.Equal(2, result.AnalyzedFiles);
        Assert.Equal(4, result.AnalyzedSymbols);
        Assert.Equal(5, result.ExcludedFiles);
        Assert.Equal(2, result.SkippedFiles);
        Assert.Equal(2, result.Breaches);
        Assert.Equal(40, result.MaxComplexity);
        Assert.Equal(20.25, result.AverageComplexity);
        // The last bucket and the breach count describe the same two symbols.
        Assert.Equal([1, 0, 1, 0, 2], result.Distribution.Select(bucket => bucket.Count));
        Assert.Equal(["1–5", "6–10", "11–15", "16–25", "> 25"], result.Distribution.Select(bucket => bucket.Label));
        Assert.Equal(["A.Huge()", "B.Big()"], result.TopBreaches.Select(breach => breach.Symbol));
        Assert.All(result.TopBreaches, breach => Assert.Equal("CA1502", breach.Rule));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    public void The_top_bucket_always_describes_exactly_the_breaches(int threshold)
    {
        var analyzed = new (string, IReadOnlyList<MemberComplexity>)[]
        {
            ("src/A.cs",
            [
                new MemberComplexity("A.Tiny()", 1, 3),
                new MemberComplexity("A.Edge()", 2, threshold),
                new MemberComplexity("A.Over()", 3, threshold + 1),
                new MemberComplexity("A.Way()", 4, threshold + 20),
            ]),
        };

        var result = CSharpComplexityScanner.Aggregate(
            analyzed, 0, 0, new ComplexityAnalysisOptions(Threshold: threshold));

        Assert.Equal(2, result.Breaches);
        Assert.Equal(result.Breaches, result.Distribution[^1].Count);
        Assert.Equal($"> {threshold}", result.Distribution[^1].Label);
        Assert.Equal($"16–{threshold}", result.Distribution[^2].Label);
        // A symbol exactly at the threshold is not a breach.
        Assert.DoesNotContain("A.Edge()", result.TopBreaches.Select(breach => breach.Symbol));
    }

    [Fact]
    public void Breach_fingerprint_identifies_the_symbol_and_its_file()
    {
        var original = CSharpComplexityScanner.Fingerprint("src/A.cs", "A.Huge(int)");

        Assert.StartsWith("sha256:", original, StringComparison.Ordinal);
        Assert.Equal(original, CSharpComplexityScanner.Fingerprint("src/A.cs", "A.Huge(int)"));
        Assert.NotEqual(original, CSharpComplexityScanner.Fingerprint("src/B.cs", "A.Huge(int)"));
        Assert.NotEqual(original, CSharpComplexityScanner.Fingerprint("src/A.cs", "A.Other(int)"));
        Assert.NotEqual(original, CSharpComplexityScanner.Fingerprint("src/A.cs", "A.Huge(string)"));
    }

    [Fact]
    public void Dashboard_reports_the_breached_symbol_with_a_line_stable_fingerprint()
    {
        var root = TemporaryRepository();
        try
        {
            WriteSource(root, "src/Tangled.cs", ComplexClass("Tangled", "Decide", 30));
            WriteSource(root, "src/Simple.cs", """
                namespace Fixture;

                public sealed class Simple
                {
                    public int Value() => 1;
                }
                """);
            RunGit(root, "add", "-A");

            var complexity = Dashboard(root).Complexity;

            Assert.Equal(2, complexity.AnalyzedFiles);
            Assert.Equal(2, complexity.AnalyzedSymbols);
            Assert.Equal(0, complexity.SkippedFiles);
            Assert.Equal(31, complexity.MaxComplexity);
            Assert.Equal(1, complexity.Breaches);
            Assert.Equal(ComplexityAnalysisOptions.Default.ConfigHash, complexity.ConfigHash);

            var breach = Assert.Single(complexity.TopBreaches);
            Assert.Equal("Tangled.Decide(int)", breach.Symbol);
            Assert.Equal("src/Tangled.cs", breach.Path);
            Assert.Equal(31, breach.Complexity);
            var fingerprint = breach.Fingerprint;
            var line = breach.Line;

            // Moving the member down the file is not new debt: the line moves, the identity does not.
            WriteSource(root, "src/Tangled.cs", "// padding\n// padding\n" + ComplexClass("Tangled", "Decide", 30));
            RunGit(root, "add", "-A");

            var moved = Assert.Single(Dashboard(root).Complexity.TopBreaches);
            Assert.Equal(fingerprint, moved.Fingerprint);
            Assert.Equal(line + 2, moved.Line);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dashboard_holds_out_generated_and_test_sources_by_default()
    {
        var root = TemporaryRepository();
        try
        {
            WriteSource(root, "src/Real.cs", ComplexClass("Real", "Decide", 30));
            WriteSource(root, "src/Machine.g.cs", ComplexClass("Machine", "Decide", 30));
            WriteSource(root, "tests/RealTests.cs", ComplexClass("RealTests", "Decide", 30));
            RunGit(root, "add", "-A");

            var complexity = Dashboard(root).Complexity;

            Assert.Equal(1, complexity.AnalyzedFiles);
            Assert.Equal(2, complexity.ExcludedFiles);
            Assert.Equal(0, complexity.SkippedFiles);
            Assert.Equal("src/Real.cs", Assert.Single(complexity.TopBreaches).Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Complexity_projection_is_identical_across_two_unchanged_runs()
    {
        var root = TemporaryRepository();
        try
        {
            WriteSource(root, "src/Tangled.cs", ComplexClass("Tangled", "Decide", 30));
            WriteSource(root, "src/Other.cs", ComplexClass("Other", "Choose", 12));
            RunGit(root, "add", "-A");

            // Separate service instances, so this compares recomputation rather than the cache.
            var first = JsonSerializer.Serialize(Dashboard(root).Complexity);
            var second = JsonSerializer.Serialize(Dashboard(root).Complexity);

            Assert.Equal(first, second);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Complexity_breaches_never_cap_a_grade()
    {
        var clean = TemporaryRepository();
        var tangled = TemporaryRepository();
        try
        {
            foreach (var root in new[] { clean, tangled })
            {
                WriteSource(root, "src/Simple.cs", """
                    namespace Fixture;

                    public sealed class Simple
                    {
                        public int Value() => 1;
                    }
                    """);
            }
            WriteSource(tangled, "src/Tangled.cs", ComplexClass("Tangled", "Decide", 30));
            RunGit(clean, "add", "-A");
            RunGit(tangled, "add", "-A");

            var cleanDashboard = Dashboard(clean);
            var tangledDashboard = Dashboard(tangled);

            Assert.Equal(0, cleanDashboard.Complexity.Breaches);
            Assert.Equal(1, tangledDashboard.Complexity.Breaches);
            // The breach changes the metric and nothing else about the grade projection.
            Assert.Equal(
                JsonSerializer.Serialize(cleanDashboard.Grades),
                JsonSerializer.Serialize(tangledDashboard.Grades));
        }
        finally
        {
            Directory.Delete(clean, true);
            Directory.Delete(tangled, true);
        }
    }

    [Fact]
    public void Raw_complexity_metrics_do_not_enter_the_architecture_prompt()
    {
        var root = TemporaryRepository();
        try
        {
            WriteSource(root, "src/Tangled.cs", ComplexClass("Tangled", "Decide", 30));
            RunGit(root, "add", "-A");

            var hierarchy = new RepositoryHierarchyCache().Get(root);
            var context = new ProjectDashboardService().ArchitectureReviewContext(root, hierarchy);

            Assert.DoesNotContain("Tangled.Decide", context, StringComparison.Ordinal);
            Assert.DoesNotContain("CA1502", context, StringComparison.Ordinal);
            Assert.DoesNotContain("complexity", context, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ProjectDashboardResponse Dashboard(string root) =>
        new ProjectDashboardService().Get(root, new RepositoryHierarchyCache().Get(root));

    /// <summary>A class whose single method has exactly <paramref name="branches"/> + 1 complexity.</summary>
    private static string ComplexClass(string type, string member, int branches)
    {
        var body = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, branches).Select(index =>
                $"        if (value == {index}) return {index};"));
        return $$"""
            namespace Fixture;

            public sealed class {{type}}
            {
                public int {{member}}(int value)
                {
            {{body}}
                    return -1;
                }
            }
            """;
    }

    private static void WriteSource(string root, string path, string content)
    {
        var absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    private static string TemporaryRepository()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "quality-studio-complexity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "--quiet");
        return root;
    }

    private static void RunGit(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
