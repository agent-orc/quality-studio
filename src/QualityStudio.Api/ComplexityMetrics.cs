using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QualityStudio.Api;

/// <summary>
/// Repository-level cyclomatic complexity read model (dossier slice S4). Distributions and the
/// breached top-N symbols are machine facts: they stand alone, carry their own threshold/config
/// hash, and never cap or otherwise influence a review grade.
/// </summary>
public sealed record ProjectComplexityMetricsResponse(
    string Rule,
    int Threshold,
    string ConfigHash,
    int AnalyzedFiles,
    int AnalyzedSymbols,
    int ExcludedFiles,
    int SkippedFiles,
    int Breaches,
    int MaxComplexity,
    double AverageComplexity,
    IReadOnlyList<ProjectDistributionBucketResponse> Distribution,
    IReadOnlyList<ProjectComplexityBreachResponse> TopBreaches);

/// <summary>A single symbol whose complexity exceeds the configured threshold.</summary>
public sealed record ProjectComplexityBreachResponse(
    string Symbol,
    string Path,
    int Line,
    int Complexity,
    string Rule,
    string Fingerprint);

/// <summary>
/// Complexity gate configuration. The threshold mirrors CA1502's default cyclomatic ceiling; test
/// and generated sources are excluded unless explicitly included, per the slice's acceptance rule.
/// </summary>
public sealed record ComplexityAnalysisOptions(
    int Threshold = 25,
    bool IncludeGenerated = false,
    bool IncludeTests = false)
{
    public static ComplexityAnalysisOptions Default { get; } = new();

    /// <summary>
    /// The gate boundary. Kept at or above the last fixed bucket edge so that the top distribution
    /// bucket and the breach list always describe the same symbols.
    /// </summary>
    public int Threshold { get; } = Threshold >= 16
        ? Threshold
        : throw new ArgumentOutOfRangeException(
            nameof(Threshold), Threshold, "Threshold must be at least 16.");

    /// <summary>Stable hash of the configuration, stored beside every finding.</summary>
    public string ConfigHash => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"csharp-complexity-v2\0threshold={Threshold}\0generated={IncludeGenerated}\0tests={IncludeTests}")));

    public bool Includes(ComplexityFileKind kind) => kind switch
    {
        ComplexityFileKind.Generated => IncludeGenerated,
        ComplexityFileKind.Test => IncludeTests,
        _ => true,
    };
}

/// <summary>Provenance of a C# file for complexity purposes.</summary>
public enum ComplexityFileKind
{
    Source,
    Generated,
    Test,
}

/// <summary>A member declaration and its measured cyclomatic complexity.</summary>
public sealed record MemberComplexity(string Symbol, int Line, int Complexity);

/// <summary>
/// Cyclomatic complexity scanner for C# sources, measured over a real Roslyn syntax tree.
/// </summary>
/// <remarks>
/// Parsing (not compiling) is what makes the numbers trustworthy: declaration shapes that defeat
/// text matching — tuple returns, nested generics, operators, <c>ref struct</c>, primary
/// constructors, expressions interpolated into strings — are ordinary syntax nodes here, so a member
/// is either measured or a parse error, never silently dropped.
/// <para>
/// Complexity is <c>1 + decision points</c>, counting <c>if · while · do · for · foreach</c>, each
/// non-default <c>case</c> label, each non-discard switch-expression arm, each <c>catch</c> and its
/// <c>when</c> filter, each ternary, and each <c>&amp;&amp; · || · ??</c>. Local functions and
/// lambdas fold into the member that declares them, matching how CA1502 attributes them. Regions
/// excluded by conditional compilation are not measured, because the parser sees them as inactive
/// text rather than code.
/// </para>
/// </remarks>
public static class CSharpComplexityScanner
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Latest, DocumentationMode.None);

    /// <summary>
    /// Measurement is a pure function of a file's bytes, and the dashboard cache is keyed on git
    /// state, so every edit anywhere otherwise re-parses every file. Memoising on content keeps a
    /// rebuild proportional to what actually changed. Bounded, because it outlives any one request.
    /// </summary>
    private const int MemoLimit = 4096;

    private static readonly ConcurrentDictionary<string, IReadOnlyList<MemberComplexity>> Memo =
        new(StringComparer.Ordinal);

    /// <summary>Classifies a C# file so generated and test sources can be held out by default.</summary>
    public static ComplexityFileKind Classify(string path, string text)
    {
        var segments = path.Split('/', '\\');
        var name = segments[^1];
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            HasGeneratedHeader(text))
            return ComplexityFileKind.Generated;

        if (name.EndsWith("Tests.cs", StringComparison.Ordinal) ||
            name.EndsWith("Test.cs", StringComparison.Ordinal) ||
            segments.Any(part =>
                part.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("test", StringComparison.OrdinalIgnoreCase)))
            return ComplexityFileKind.Test;

        return ComplexityFileKind.Source;
    }

    /// <summary>
    /// Measures every method-like member declaration in <paramref name="text"/>. Local functions and
    /// lambdas fold into the member that declares them rather than reporting separately.
    /// </summary>
    public static IReadOnlyList<MemberComplexity> Scan(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (Memo.TryGetValue(key, out var cached)) return cached;

        var root = CSharpSyntaxTree.ParseText(text, ParseOptions).GetRoot();
        var members = new List<MemberComplexity>();
        foreach (var member in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            members.Add(new MemberComplexity(Symbol(member), LineOf(member), Measure(member)));

        if (Memo.Count >= MemoLimit) Memo.Clear();
        Memo[key] = members;
        return members;
    }

    /// <summary>Composes the dashboard projection from per-file scan results.</summary>
    public static ProjectComplexityMetricsResponse Aggregate(
        IEnumerable<(string Path, IReadOnlyList<MemberComplexity> Members)> analyzed,
        int excludedFiles,
        int skippedFiles,
        ComplexityAnalysisOptions options)
    {
        var labels = BucketLabels(options.Threshold);
        var counts = new int[labels.Length];
        var breaches = new List<ProjectComplexityBreachResponse>();
        var files = 0;
        var symbols = 0;
        var total = 0L;
        var max = 0;

        foreach (var (path, members) in analyzed)
        {
            files++;
            foreach (var member in members)
            {
                symbols++;
                total += member.Complexity;
                max = Math.Max(max, member.Complexity);
                counts[BucketIndex(member.Complexity, options.Threshold)]++;
                // CA1502 fires above the threshold, which keeps the top bucket and the breach count
                // describing exactly the same symbols.
                if (member.Complexity <= options.Threshold) continue;
                breaches.Add(new ProjectComplexityBreachResponse(
                    member.Symbol,
                    path,
                    member.Line,
                    member.Complexity,
                    "CA1502",
                    Fingerprint(path, member.Symbol)));
            }
        }

        return new ProjectComplexityMetricsResponse(
            "CA1502",
            options.Threshold,
            options.ConfigHash,
            files,
            symbols,
            excludedFiles,
            skippedFiles,
            breaches.Count,
            max,
            symbols == 0 ? 0 : Math.Round((double)total / symbols, 2),
            labels.Select((label, index) =>
                new ProjectDistributionBucketResponse(label, counts[index])).ToArray(),
            breaches
                .OrderByDescending(breach => breach.Complexity)
                .ThenBy(breach => breach.Path, StringComparer.Ordinal)
                .ThenBy(breach => breach.Symbol, StringComparer.Ordinal)
                .Take(20)
                .ToArray());
    }

    /// <summary>
    /// Identity of a breach: the file and the overload-qualified symbol, deliberately excluding the
    /// line so a finding survives edits elsewhere in the file rather than resurfacing as new debt.
    /// </summary>
    public static string Fingerprint(string path, string symbol) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "csharp-complexity-v2\0" + path + "\0" + symbol)));

    internal static string[] BucketLabels(int threshold) =>
        ["1–5", "6–10", "11–15", $"16–{threshold}", $"> {threshold}"];

    private static int BucketIndex(int complexity, int threshold) =>
        complexity <= 5 ? 0
        : complexity <= 10 ? 1
        : complexity <= 15 ? 2
        : complexity <= threshold ? 3
        : 4;

    private static bool HasGeneratedHeader(string text)
    {
        var head = text.Length <= 512 ? text : text[..512];
        return head.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The declaration's own line, skipping any attribute lists above it.</summary>
    private static int LineOf(BaseMethodDeclarationSyntax member)
    {
        var anchor = member.AttributeLists.Count == 0
            ? member.GetFirstToken()
            : member.AttributeLists[^1].CloseBracketToken.GetNextToken();
        return anchor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    /// <summary>
    /// Type-qualified and overload-qualified name, so two overloads never share an identity.
    /// </summary>
    private static string Symbol(BaseMethodDeclarationSyntax member)
    {
        var name = member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.ValueText,
            OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.ValueText,
            ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type.ToString(),
            _ => member.Kind().ToString(),
        };
        var owners = member.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(type => type.Identifier.ValueText)
            .Reverse();
        var qualified = string.Join('.', owners.Append(name));
        var parameters = member.ParameterList.Parameters
            .Select(parameter => parameter.Type?.ToString() ?? "?");
        return $"{qualified}({string.Join(", ", parameters)})";
    }

    private static int Measure(SyntaxNode member)
    {
        var points = 0;
        foreach (var node in member.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case CatchClauseSyntax:
                case CatchFilterClauseSyntax:
                case ConditionalExpressionSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                    points++;
                    break;
                case SwitchExpressionArmSyntax arm when arm.Pattern is not DiscardPatternSyntax:
                    points++;
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression)
                                                        || binary.IsKind(SyntaxKind.LogicalOrExpression)
                                                        || binary.IsKind(SyntaxKind.CoalesceExpression):
                    points++;
                    break;
            }
        }
        return points + 1;
    }
}
