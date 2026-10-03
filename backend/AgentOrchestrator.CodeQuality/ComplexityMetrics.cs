using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Complexity of one function unit: a method, accessor, top-level function or class member.</summary>
public sealed record FunctionComplexity(string Name, int Line, int EndLine, int Cyclomatic, int Cognitive);

/// <summary>
/// Complexity of one source file. File values are sums over its function units, so a file with many
/// small functions and a file with one large function can share a sum and differ in their maximum.
/// </summary>
public sealed record FileComplexity(
    string Path,
    string Language,
    int Cyclomatic,
    int Cognitive,
    int MaxCyclomatic,
    int MaxCognitive,
    IReadOnlyList<FunctionComplexity> Functions)
{
    /// <summary>The per-function cognitive complexity above which a function counts as hard to follow.</summary>
    public const int CognitiveThreshold = 15;

    /// <summary>
    /// The file's complexity contribution to the risk score, 0-100: its most complex function against
    /// twice <see cref="CognitiveThreshold"/>. The maximum, not the sum, because one tangled function
    /// is what makes a file hard to change and to test; a long file of simple functions is not.
    /// </summary>
    public int Pressure => Math.Min(100, (int)Math.Round(MaxCognitive * 100d / (2 * CognitiveThreshold),
        MidpointRounding.AwayFromZero));
}

/// <summary>
/// Cyclomatic and cognitive complexity from syntax alone: Roslyn's parser for C#, a tokenizer for
/// TypeScript and JavaScript. No compilation, no type information, no Node process, so the metric is
/// deterministic, offline, and cheap enough to compute for every file of a repository on demand.
/// <para>
/// Both languages follow one rule set (see <c>docs/coverage-and-risk.md</c>): cyclomatic complexity is
/// one plus each branch point (<c>if</c>, loop, <c>case</c>, <c>catch</c>, conditional operator,
/// <c>&amp;&amp;</c>, <c>||</c>, <c>??</c>); cognitive complexity follows the SonarSource definition -
/// structural increments weighted by nesting, one increment per sequence of like logical operators, and
/// lambdas, nested functions and callbacks fold into the function that declares them.
/// </para>
/// </summary>
public static class ComplexityAnalyzer
{
    public const string Version = "1.0.0";

    /// <summary>Whether <paramref name="path"/> is a source file this analyzer measures.</summary>
    public static bool Supports(string path) => LanguageOf(path) is not null;

    public static string? LanguageOf(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)) return null;
        return System.IO.Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".mts" or ".cts" => "typescript",
            ".js" or ".mjs" or ".cjs" => "javascript",
            _ => null,
        };
    }

    public static FileComplexity? Analyze(string path, string text)
    {
        var language = LanguageOf(path);
        if (language is null) return null;
        var functions = language == "csharp" ? CSharpComplexity.Analyze(text) : ScriptComplexity.Analyze(text);
        return new FileComplexity(
            path.Replace('\\', '/'),
            language,
            functions.Sum(function => function.Cyclomatic),
            functions.Sum(function => function.Cognitive),
            functions.Select(function => function.Cyclomatic).DefaultIfEmpty().Max(),
            functions.Select(function => function.Cognitive).DefaultIfEmpty().Max(),
            functions);
    }
}

/// <summary>
/// Remembers file complexity by path, size and write time, so the risk view recomputes only the files
/// that changed since the last request. One instance serves every repository of a host.
/// </summary>
public sealed class ComplexityCache
{
    /// <summary>Files larger than this are not measured; generated bundles would dominate the parse time.</summary>
    public const long MaximumFileBytes = 1_000_000;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public FileComplexity? Get(string repositoryRoot, string relativePath)
    {
        if (!ComplexityAnalyzer.Supports(relativePath)) return null;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(repositoryRoot,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var info = new FileInfo(full);
        if (!info.Exists || info.Length > MaximumFileBytes) return null;
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
        if (entries.TryGetValue(full, out var cached) && cached.Stamp == stamp) return cached.Value;
        FileComplexity? value;
        try
        {
            value = ComplexityAnalyzer.Analyze(relativePath, File.ReadAllText(full));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        entries[full] = new Entry(stamp, value);
        return value;
    }

    /// <summary>Measures many files in parallel; unsupported or unreadable files are absent from the result.</summary>
    public IReadOnlyDictionary<string, FileComplexity> GetMany(string repositoryRoot, IEnumerable<string> relativePaths)
    {
        var result = new ConcurrentDictionary<string, FileComplexity>(StringComparer.Ordinal);
        Parallel.ForEach(relativePaths.Where(ComplexityAnalyzer.Supports).Distinct(StringComparer.Ordinal), path =>
        {
            if (Get(repositoryRoot, path) is { } value) result[path] = value;
        });
        return result;
    }

    private sealed record Entry((long Length, long Ticks) Stamp, FileComplexity? Value);
}

/// <summary>Roslyn-syntax complexity for C#. Lambdas and nested local functions fold into their member.</summary>
internal static class CSharpComplexity
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview, DocumentationMode.None);

    public static IReadOnlyList<FunctionComplexity> Analyze(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions);
        var root = tree.GetCompilationUnitRoot();
        var functions = new List<FunctionComplexity>();
        foreach (var node in root.DescendantNodes(descendant =>
                     descendant is not (BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax
                         or GlobalStatementSyntax)))
        {
            SyntaxNode? body = node switch
            {
                BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
                AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
                PropertyDeclarationSyntax property => property.ExpressionBody,
                IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
                _ => null,
            };
            if (body is not null) functions.Add(Measure(Name(node), node, [body]));
        }

        // Top-level statements are one unit; their local functions are the program's methods, so each
        // is its own unit rather than inflating the entry point.
        var globals = root.Members.OfType<GlobalStatementSyntax>().ToArray();
        var localFunctions = globals.Select(global => global.Statement).OfType<LocalFunctionStatementSyntax>().ToArray();
        foreach (var local in localFunctions)
            functions.Add(Measure(local.Identifier.Text, local, [local]));
        var statements = globals.Select(global => global.Statement)
            .Where(statement => statement is not LocalFunctionStatementSyntax).ToArray();
        if (statements.Length > 0)
            functions.Add(Measure("<top-level>", statements[0], statements, statements[^1]));

        return functions.OrderBy(function => function.Line).ThenBy(function => function.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static FunctionComplexity Measure(
        string name, SyntaxNode anchor, IReadOnlyList<SyntaxNode> bodies, SyntaxNode? last = null)
    {
        var walker = new Walker();
        foreach (var body in bodies)
        {
            // A top-level local function is measured from its body, so its own declaration is no nesting.
            if (body is LocalFunctionStatementSyntax local)
            {
                if (local.Body is not null) walker.Visit(local.Body);
                if (local.ExpressionBody is not null) walker.Visit(local.ExpressionBody);
            }
            else walker.Visit(body);
        }
        var span = anchor.GetLocation().GetLineSpan();
        var end = (last ?? anchor).GetLocation().GetLineSpan();
        return new FunctionComplexity(name, span.StartLinePosition.Line + 1, end.EndLinePosition.Line + 1,
            1 + walker.Cyclomatic, walker.Cognitive);
    }

    private static string Name(SyntaxNode node)
    {
        var member = node switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax constructor => constructor.Modifiers.Any(SyntaxKind.StaticKeyword)
                ? "cctor" : "ctor",
            DestructorDeclarationSyntax => "Finalize",
            OperatorDeclarationSyntax op => "operator " + op.OperatorToken.Text,
            ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
            AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax property } accessor =>
                property.Identifier.Text + "." + accessor.Keyword.Text,
            AccessorDeclarationSyntax { Parent.Parent: IndexerDeclarationSyntax } accessor => "this[]." + accessor.Keyword.Text,
            AccessorDeclarationSyntax { Parent.Parent: EventDeclarationSyntax @event } accessor =>
                @event.Identifier.Text + "." + accessor.Keyword.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text + ".get",
            IndexerDeclarationSyntax => "this[].get",
            _ => node.Kind().ToString(),
        };
        var types = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(type => type.Identifier.Text).Reverse();
        var container = string.Join('.', types);
        return container.Length == 0 ? member : container + "." + member;
    }

    private sealed class Walker : CSharpSyntaxWalker
    {
        private int nesting;

        public int Cyclomatic { get; private set; }

        public int Cognitive { get; private set; }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            Cyclomatic++;
            // `else if` continues the chain: it scores like `else`, without a nesting increment.
            Cognitive += node.Parent is ElseClauseSyntax ? 1 : 1 + nesting;
            Visit(node.Condition);
            Nested(node.Statement);
            if (node.Else is { } @else)
            {
                if (@else.Statement is IfStatementSyntax elseIf) Visit(elseIf);
                else
                {
                    Cognitive++;
                    Nested(@else.Statement);
                }
            }
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            Cognitive += 1 + nesting;
            Visit(node.Expression);
            nesting++;
            foreach (var section in node.Sections)
            {
                Cyclomatic += section.Labels.Count(label => label is not DefaultSwitchLabelSyntax);
                foreach (var statement in section.Statements) Visit(statement);
            }
            nesting--;
        }

        public override void VisitSwitchExpression(SwitchExpressionSyntax node)
        {
            Cognitive += 1 + nesting;
            Visit(node.GoverningExpression);
            nesting++;
            foreach (var arm in node.Arms)
            {
                if (arm.Pattern is not DiscardPatternSyntax) Cyclomatic++;
                Visit(arm);
            }
            nesting--;
        }

        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            Cyclomatic++;
            Cognitive += 1 + nesting;
            Visit(node.Condition);
            Nested(node.WhenTrue);
            Nested(node.WhenFalse);
        }

        public override void VisitForStatement(ForStatementSyntax node) => Loop(node, node.Statement);

        public override void VisitForEachStatement(ForEachStatementSyntax node) => Loop(node, node.Statement);

        public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node) => Loop(node, node.Statement);

        public override void VisitWhileStatement(WhileStatementSyntax node) => Loop(node, node.Statement);

        public override void VisitDoStatement(DoStatementSyntax node) => Loop(node, node.Statement);

        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            Cyclomatic++;
            Cognitive += 1 + nesting;
            if (node.Filter is not null) Visit(node.Filter);
            Nested(node.Block);
        }

        public override void VisitGotoStatement(GotoStatementSyntax node)
        {
            Cognitive++;
            base.VisitGotoStatement(node);
        }

        public override void VisitBinaryPattern(BinaryPatternSyntax node)
        {
            Cyclomatic++;
            base.VisitBinaryPattern(node);
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.CoalesceAssignmentExpression)) Cyclomatic++;
            base.VisitAssignmentExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (!IsLogical(node) && !node.IsKind(SyntaxKind.CoalesceExpression))
            {
                base.VisitBinaryExpression(node);
                return;
            }
            Cyclomatic++;
            if (IsLogical(node) && !IsLogical(Unparenthesized(node.Parent)))
            {
                // The outermost operator of a sequence scores it once per change of operator.
                var operators = new List<SyntaxKind>();
                Collect(node, operators);
                Cognitive += 1 + operators.Zip(operators.Skip(1)).Count(pair => pair.First != pair.Second);
            }
            base.VisitBinaryExpression(node);
        }

        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) =>
            Nested(node.Body);

        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => Nested(node.Body);

        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => Nested(node.Body);

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            if (node.Body is not null) Nested(node.Body);
            if (node.ExpressionBody is not null) Nested(node.ExpressionBody);
        }

        private void Loop(SyntaxNode node, StatementSyntax statement)
        {
            Cyclomatic++;
            Cognitive += 1 + nesting;
            foreach (var child in node.ChildNodes())
                if (child != statement) Visit(child);
            Nested(statement);
        }

        private void Nested(SyntaxNode node)
        {
            nesting++;
            Visit(node);
            nesting--;
        }

        private static bool IsLogical(SyntaxNode? node) =>
            node is BinaryExpressionSyntax binary &&
            binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression;

        private static SyntaxNode? Unparenthesized(SyntaxNode? node)
        {
            while (node is ParenthesizedExpressionSyntax) node = node.Parent;
            return node;
        }

        private static void Collect(ExpressionSyntax expression, List<SyntaxKind> operators)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            if (expression is not BinaryExpressionSyntax binary || !IsLogical(binary)) return;
            Collect(binary.Left, operators);
            operators.Add(binary.Kind());
            Collect(binary.Right, operators);
        }
    }
}

/// <summary>
/// Token-level complexity for TypeScript and JavaScript. A function unit is a class member or a
/// top-level declaration that holds function code; callbacks and nested functions fold into it and
/// deepen its nesting. Statements outside any function form one <c>&lt;top-level&gt;</c> unit.
/// <para>
/// Known approximations of a tokenizer without a grammar: a brace-less <c>if</c>/loop body adds no
/// nesting, conditional types in type positions count as conditional operators, and JSX is not
/// supported (<c>.tsx</c>/<c>.jsx</c> are not measured).
/// </para>
/// </summary>
internal static class ScriptComplexity
{
    private enum TokenKind { Word, Punct, String, Number, Regex }

    private readonly record struct Token(TokenKind Kind, string Text, int Line);

    private enum ScopeKind { Container, Type, Function, Control, Block }

    private sealed class Scope(ScopeKind kind, int nesting, string? className = null)
    {
        public ScopeKind Kind { get; } = kind;
        public int Nesting { get; } = nesting;
        public string? ClassName { get; } = className;
        public Member? Member { get; set; }
    }

    private sealed class Member(string name, int line, bool ignored)
    {
        public string Name { get; } = name;
        public int Line { get; } = line;
        public int EndLine { get; set; } = line;
        public bool Ignored { get; } = ignored;
        public bool IsFunction { get; set; }
        public bool HasInitializer { get; set; }
        public bool ExpressionArrow { get; set; }
        public int Decisions { get; set; }
        public int Cognitive { get; set; }
    }

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "export", "default", "public", "private", "protected", "static", "readonly", "abstract", "override",
        "async", "const", "let", "var", "function", "accessor", "*",
    };

    private static readonly HashSet<string> StatementKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "do", "switch", "try", "return", "throw", "await", "new", "void", "delete", "typeof",
        "(", "[", "!", "{", "`", "super", "this",
    };

    private static readonly HashSet<string> RegexPrefixKeywords = new(StringComparer.Ordinal)
    {
        "return", "typeof", "case", "in", "of", "new", "delete", "void", "throw", "instanceof", "yield", "await", "else", "do",
    };

    private static readonly HashSet<string> ContinuationTokens = new(StringComparer.Ordinal)
    {
        "=", ",", "(", "[", "{", ".", "?.", "?", ":", "&&", "||", "??", "+", "-", "*", "/", "%", "=>", "|", "&",
        "<", ">", "!", "==", "===", "!=", "!==", "<=", ">=", "+=", "-=", "*=", "/=", "&&=", "||=", "??=", "@",
        "extends", "implements", "new", "return", "typeof", "keyof", "in", "of", "instanceof", "as",
    };

    private static readonly string[] Operators =
    [
        "...", "===", "!==", "&&=", "||=", "??=", "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "**",
    ];

    public static IReadOnlyList<FunctionComplexity> Analyze(string text) => new Scanner(Tokenize(text)).Run();

    /// <summary>One pass over a file's tokens, tracking scopes, the current member, and its increments.</summary>
    private sealed class Scanner(List<Token> tokens)
    {
        private readonly Dictionary<int, int> matching = MatchBrackets(tokens);
        private readonly List<FunctionComplexity> functions = [];
        private readonly Member topLevel = new("<top-level>", 0, ignored: false);
        private readonly Stack<Scope> scopes = new([new Scope(ScopeKind.Container, 0)]);
        private readonly HashSet<int> controlParens = [];
        private readonly HashSet<int> functionBodies = [];
        private readonly HashSet<int> typeBraces = [];
        // The last logical operator per bracket level, so `a && b || c` scores a change of operator.
        private readonly Stack<string?> logicalFrames = new([null]);
        private string? pendingControl;
        private int pendingControlAt = -1;
        private ScopeKind? pendingDeclaration;
        private string? pendingClassName;

        public IReadOnlyList<FunctionComplexity> Run()
        {
            for (var index = 0; index < tokens.Count; index++) Step(index);
            foreach (var scope in scopes.Where(candidate => candidate.Kind == ScopeKind.Container && candidate.Member is not null))
                Finish(scope);
            if (topLevel.Decisions > 0 || topLevel.Cognitive > 0)
                functions.Add(new FunctionComplexity("<top-level>", 1, tokens.Count == 0 ? 1 : tokens[^1].Line,
                    1 + topLevel.Decisions, topLevel.Cognitive));
            return functions.OrderBy(function => function.Line).ThenBy(function => function.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private void Step(int index)
        {
            var token = tokens[index];
            if (scopes.Peek().Kind == ScopeKind.Type)
            {
                SkipType(token, index);
                return;
            }

            // A control header or `else`/`do`/`try` opens a nesting block only when `{` follows at once.
            if (pendingControl is not null && pendingControlAt != index - 1) pendingControl = null;
            var target = TrackMember(token, index);
            if (token.Kind == TokenKind.Word) OnWord(token, index, target);
            else if (token.Kind == TokenKind.Punct) OnPunct(token, index, target);
        }

        private void SkipType(Token token, int index)
        {
            if (token.Kind != TokenKind.Punct) return;
            if (token.Text == "{") scopes.Push(new Scope(ScopeKind.Type, 0));
            if (token.Text != "}") return;
            scopes.Pop();
            // An inline object type (`): { ok: boolean } {`) does not end the declaration it annotates.
            var literal = matching.TryGetValue(index, out var open) && typeBraces.Contains(open);
            AfterClose(index, endsDeclaration: !literal);
        }

        /// <summary>
        /// A statement in a class or module body starts a member, and everything until it ends is its
        /// code. Returns the member that increments at this token count towards.
        /// </summary>
        private Member TrackMember(Token token, int index)
        {
            var scope = scopes.Peek();
            if (scope.Kind == ScopeKind.Container)
            {
                if (scope.Member is not null && logicalFrames.Count == 1 && EndsByNewline(tokens, index, scope))
                    Finish(scope);
                if (scope.Member is null && !(token.Kind == TokenKind.Punct && token.Text is ";" or "}"))
                {
                    scope.Member = Begin(tokens, index, scope);
                    pendingDeclaration = null;
                    pendingClassName = null;
                }
            }

            var member = InnermostContainer.Member;
            if (member is null) return topLevel;
            member.EndLine = token.Line;
            // An ignored member (import, type alias) still absorbs its tokens, but they count nowhere.
            return member;
        }

        // Stack<T> enumerates from the top, so the first container is the innermost one: a class
        // method's increments go to that method, not to the member enclosing the class.
        private Scope InnermostContainer => scopes.First(candidate => candidate.Kind is ScopeKind.Container);

        private void OnWord(Token token, int index, Member target)
        {
            if (index > 0 && tokens[index - 1].Text is "." or "?.") return;
            switch (token.Text)
            {
                case "if" or "for" or "while" or "switch" or "catch":
                    OnControlKeyword(token, index, target);
                    break;
                case "else" when index + 1 < tokens.Count && tokens[index + 1].Text != "if":
                    target.Cognitive++;
                    SetControl("else", index);
                    break;
                case "do":
                    target.Decisions++;
                    target.Cognitive += 1 + Nesting;
                    SetControl("do", index);
                    break;
                case "try" or "finally":
                    SetControl("block", index);
                    break;
                case "case":
                    target.Decisions++;
                    break;
                case "function":
                    target.IsFunction = true;
                    break;
                case "class" when !IsPropertyName(tokens, index):
                    pendingDeclaration = ScopeKind.Container;
                    pendingClassName = index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Word &&
                                       tokens[index + 1].Text is not ("extends" or "implements")
                        ? tokens[index + 1].Text : "<anonymous>";
                    break;
                case "interface" or "enum" when !IsPropertyName(tokens, index):
                    pendingDeclaration = ScopeKind.Type;
                    break;
                case "namespace" or "module" when scopes.Peek().Kind == ScopeKind.Container &&
                                                  index + 1 < tokens.Count &&
                                                  tokens[index + 1].Kind is TokenKind.Word or TokenKind.String:
                    pendingDeclaration = ScopeKind.Container;
                    pendingClassName = null;
                    break;
            }
        }

        private void OnControlKeyword(Token token, int index, Member target)
        {
            if (token.Text == "while" && IsDoWhile(tokens, index, matching)) return;
            var open = NextIndex(tokens, index, "(");
            if (open >= 0) controlParens.Add(open);
            if (token.Text != "switch") target.Decisions++;
            var elseIf = token.Text == "if" && index > 0 && tokens[index - 1].Text == "else";
            target.Cognitive += elseIf ? 1 : 1 + Nesting;
            if (token.Text == "catch" && open < 0) SetControl("catch", index);
        }

        private void OnPunct(Token token, int index, Member target)
        {
            switch (token.Text)
            {
                case "(" or "[":
                    logicalFrames.Push(null);
                    break;
                case ")" or "]":
                    if (logicalFrames.Count > 1) logicalFrames.Pop();
                    if (token.Text == ")") OnCloseParen(index);
                    break;
                case "=":
                    if (scopes.Peek().Kind == ScopeKind.Container) target.HasInitializer = true;
                    break;
                case "=>":
                    OnArrow(index, target);
                    break;
                case "?" when index + 1 < tokens.Count && tokens[index + 1].Text is ":" or ")" or "," or "=" or ";":
                    // An optional marker (`a?: T`, `(a?)`), not a conditional.
                    break;
                case "?":
                    target.Decisions++;
                    target.Cognitive += 1 + Nesting;
                    ResetLogical();
                    break;
                case "&&" or "||":
                    target.Decisions++;
                    if (logicalFrames.Pop() != token.Text) target.Cognitive++;
                    logicalFrames.Push(token.Text);
                    break;
                case "??" or "&&=" or "||=" or "??=":
                    target.Decisions++;
                    break;
                case ";" or ",":
                    ResetLogical();
                    var scope = scopes.Peek();
                    if (token.Text == ";" && scope.Kind == ScopeKind.Container && scope.Member is not null) Finish(scope);
                    break;
                case "{":
                    ResetLogical();
                    scopes.Push(OpenBrace(index, target));
                    pendingControl = null;
                    break;
                case "}":
                    ResetLogical();
                    CloseBrace(index);
                    pendingControl = null;
                    break;
            }
        }

        private void OnCloseParen(int index)
        {
            if (matching.TryGetValue(index, out var open) && controlParens.Contains(open)) SetControl("control", index);
            else ClassifyAfterParameters(tokens, index, matching, functionBodies, typeBraces);
        }

        private void OnArrow(int index, Member target)
        {
            // `x = () => ...` or a callback inside a member's braces is function code; a field type
            // `handler: (x: T) => void` is not.
            if (target.HasInitializer || scopes.Peek().Kind != ScopeKind.Container) target.IsFunction = true;
            if (index + 1 < tokens.Count && tokens[index + 1].Text == "{") functionBodies.Add(index + 1);
            else if (scopes.Peek().Kind == ScopeKind.Container) target.ExpressionArrow = true;
        }

        private Scope OpenBrace(int index, Member target)
        {
            var nesting = Nesting;
            if (typeBraces.Contains(index)) return new Scope(ScopeKind.Type, 0);
            if (pendingDeclaration is ScopeKind declaration)
            {
                pendingDeclaration = null;
                return declaration == ScopeKind.Type
                    ? new Scope(ScopeKind.Type, 0)
                    : new Scope(ScopeKind.Container, 0, pendingClassName);
            }
            if (pendingControl is "control" or "else" or "do" or "catch") return new Scope(ScopeKind.Control, nesting + 1);
            if (pendingControl is "block" || !functionBodies.Contains(index)) return new Scope(ScopeKind.Block, nesting);
            target.IsFunction = true;
            // The unit's own body is nesting zero; every function inside it nests one deeper.
            var insideFunction = target.ExpressionArrow ||
                                 scopes.TakeWhile(candidate => candidate.Kind != ScopeKind.Container)
                                     .Any(candidate => candidate.Kind == ScopeKind.Function);
            return new Scope(ScopeKind.Function, insideFunction ? nesting + 1 : 0);
        }

        private void CloseBrace(int index)
        {
            if (scopes.Count <= 1) return;
            var closed = scopes.Pop();
            if (closed.Kind == ScopeKind.Container && closed.Member is not null) Finish(closed);
            AfterClose(index, closed.Kind is ScopeKind.Function or ScopeKind.Container or ScopeKind.Control);
        }

        private int Nesting => scopes.Peek().Nesting;

        private void SetControl(string kind, int at)
        {
            pendingControl = kind;
            pendingControlAt = at;
        }

        private void ResetLogical()
        {
            logicalFrames.Pop();
            logicalFrames.Push(null);
        }

        private void Finish(Scope scope)
        {
            var finished = scope.Member!;
            scope.Member = null;
            if (finished.Ignored) return;
            if (finished.Name == "<top-level>" || !finished.IsFunction)
            {
                topLevel.Decisions += finished.Decisions;
                topLevel.Cognitive += finished.Cognitive;
                return;
            }
            var name = scope.ClassName is null ? finished.Name : scope.ClassName + "." + finished.Name;
            functions.Add(new FunctionComplexity(name, finished.Line, finished.EndLine,
                1 + finished.Decisions, finished.Cognitive));
        }

        private void AfterClose(int index, bool endsDeclaration)
        {
            var parent = scopes.Peek();
            if (parent.Kind != ScopeKind.Container || parent.Member is null) return;
            // A closing body ends a declaration; a closing object literal does not end `x = { ... };`.
            var next = index + 1 < tokens.Count ? tokens[index + 1] : default;
            if (endsDeclaration && next.Text is not ("," or ")" or "." or "?." or ";" or "(" or "[" or "as"))
                Finish(parent);
        }
    }

    private static Member Begin(IReadOnlyList<Token> tokens, int index, Scope scope)
    {
        var start = tokens[index];
        var cursor = index;
        // Decorators: `@Input()` or `@Component({...})` before the member they decorate.
        while (cursor < tokens.Count && tokens[cursor].Text == "@")
        {
            cursor += 2;
            while (cursor < tokens.Count && tokens[cursor].Text == ".") cursor += 2;
            if (cursor < tokens.Count && tokens[cursor].Text == "(") cursor = SkipBalanced(tokens, cursor);
        }
        var sawDeclarationKeyword = false;
        while (cursor < tokens.Count && Modifiers.Contains(tokens[cursor].Text) &&
               !(cursor + 1 < tokens.Count && tokens[cursor + 1].Text is "(" or "=" or ":" or ";" or "?" or "<"))
        {
            if (tokens[cursor].Text is "const" or "let" or "var" or "function" or "export") sawDeclarationKeyword = true;
            cursor++;
        }
        if (cursor < tokens.Count && tokens[cursor].Text is "get" or "set" && cursor + 1 < tokens.Count &&
            tokens[cursor + 1].Kind == TokenKind.Word)
            cursor++;
        if (cursor >= tokens.Count) return new Member("<top-level>", start.Line, ignored: false);
        var head = tokens[cursor];
        // Imports, ambient declarations and type aliases carry no executable code; `type X = () => T`
        // must not become a function.
        var following = cursor + 1 < tokens.Count ? tokens[cursor + 1] : default;
        if (head.Text is "import" && following.Text is not ("(" or ".") ||
            head.Text is "declare" && following.Kind == TokenKind.Word ||
            head.Text is "type" && following.Kind == TokenKind.Word)
            return new Member("", start.Line, ignored: true);
        var inClass = scope.ClassName is not null;
        if (!inClass && !sawDeclarationKeyword) return new Member("<top-level>", start.Line, ignored: false);
        if (head.Kind == TokenKind.String || head.Kind == TokenKind.Number)
            return new Member(head.Text, start.Line, ignored: false);
        if (head.Kind == TokenKind.Word && !StatementKeywords.Contains(head.Text))
            return new Member(head.Text == "#" ? "#" : head.Text, start.Line, ignored: false);
        if (head.Text == "#" && cursor + 1 < tokens.Count) return new Member("#" + tokens[cursor + 1].Text, start.Line, false);
        if (head.Text == "[") return new Member("[computed]", start.Line, ignored: false);
        return new Member("<top-level>", start.Line, ignored: false);
    }

    /// <summary>
    /// Automatic semicolon insertion at class or module level: a member ends at a line break unless the
    /// previous token or the next one continues the expression.
    /// </summary>
    private static bool EndsByNewline(IReadOnlyList<Token> tokens, int index, Scope scope)
    {
        if (index == 0 || tokens[index].Line == tokens[index - 1].Line) return false;
        var previous = tokens[index - 1];
        var current = tokens[index];
        if (previous.Kind == TokenKind.Punct && ContinuationTokens.Contains(previous.Text)) return false;
        if (previous.Kind == TokenKind.Word && ContinuationTokens.Contains(previous.Text)) return false;
        if (current.Kind == TokenKind.Punct && current.Text is "." or "?." or ")" or "]" or "," or "=>" or "?" or ":"
                or "&&" or "||" or "??" or "=" or "+" or "-" or "*" or "/" or "|" or "&" or "{" or "(" or "<" or ">")
            return false;
        if (current.Kind == TokenKind.Word && current.Text is "extends" or "implements" or "as" or "in" or "of" or "instanceof")
            return false;
        return scope.Member is not null;
    }

    /// <summary>
    /// After a non-control <c>)</c>: a following <c>{</c> or a return type then <c>{</c> is a function
    /// body; a <c>{</c> right after <c>:</c> or <c>|</c> inside that return type is an object type.
    /// </summary>
    private static void ClassifyAfterParameters(IReadOnlyList<Token> tokens, int index,
        IReadOnlyDictionary<int, int> matching, HashSet<int> functionBodies, HashSet<int> typeBraces)
    {
        var next = index + 1;
        if (next >= tokens.Count) return;
        if (tokens[next].Text == "{")
        {
            functionBodies.Add(next);
            return;
        }
        if (tokens[next].Text != ":") return;
        var depth = 0;
        for (var cursor = next + 1; cursor < tokens.Count; cursor++)
        {
            var text = tokens[cursor].Text;
            if (tokens[cursor].Kind != TokenKind.Punct) continue;
            if (text is "(" or "[" or "<") depth++;
            else if (text is ")" or "]" or ">") { if (--depth < 0) return; }
            else if (text == "=>" && depth == 0) return;
            else if (text is ";" or "=" or "," && depth == 0) return;
            else if (text == "{")
            {
                var before = tokens[cursor - 1].Text;
                if (depth > 0 || before is ":" or "|" or "&" or "<" or "," or "(" or "[" or "=>" or "?")
                {
                    typeBraces.Add(cursor);
                    if (!matching.TryGetValue(cursor, out var close)) return;
                    cursor = close;
                    continue;
                }
                functionBodies.Add(cursor);
                return;
            }
        }
    }

    private static bool IsPropertyName(IReadOnlyList<Token> tokens, int index) =>
        index + 1 < tokens.Count && tokens[index + 1].Text is ":" or "," or "=" or ")" or "(" or ";" or "?";

    private static bool IsDoWhile(IReadOnlyList<Token> tokens, int index, IReadOnlyDictionary<int, int> matching) =>
        index > 0 && tokens[index - 1].Text == "}" && matching.TryGetValue(index - 1, out var open) && open > 0 &&
        tokens[open - 1].Text == "do";

    private static int NextIndex(IReadOnlyList<Token> tokens, int index, string text)
    {
        for (var cursor = index + 1; cursor < Math.Min(tokens.Count, index + 3); cursor++)
            if (tokens[cursor].Text == text) return cursor;
        return -1;
    }

    private static int SkipBalanced(IReadOnlyList<Token> tokens, int open)
    {
        var depth = 0;
        for (var cursor = open; cursor < tokens.Count; cursor++)
        {
            if (tokens[cursor].Kind != TokenKind.Punct) continue;
            if (tokens[cursor].Text is "(" or "[" or "{") depth++;
            else if (tokens[cursor].Text is ")" or "]" or "}" && --depth == 0) return cursor + 1;
        }
        return tokens.Count;
    }

    /// <summary>Both directions of every matched bracket pair, by token index.</summary>
    private static Dictionary<int, int> MatchBrackets(IReadOnlyList<Token> tokens)
    {
        var result = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != TokenKind.Punct) continue;
            switch (tokens[index].Text)
            {
                case "(" or "[" or "{":
                    stack.Push(index);
                    break;
                case ")" or "]" or "}":
                    if (stack.Count == 0) break;
                    var open = stack.Pop();
                    result[open] = index;
                    result[index] = open;
                    break;
            }
        }
        return result;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var templateDepths = new Stack<int>();
        var braceDepth = 0;
        var line = 1;
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\n')
            {
                line++;
                index++;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                line += Count(text, index, end, '\n');
                index = end;
                continue;
            }
            if (character is '"' or '\'')
            {
                var start = line;
                index = SkipQuoted(text, index, character, ref line);
                tokens.Add(new Token(TokenKind.String, "\"\"", start));
                continue;
            }
            if (character == '`')
            {
                tokens.Add(new Token(TokenKind.String, "``", line));
                index = ScanTemplate(text, index + 1, ref line, templateDepths, braceDepth);
                continue;
            }
            if (character == '}' && templateDepths.Count > 0 && templateDepths.Peek() == braceDepth)
            {
                templateDepths.Pop();
                tokens.Add(new Token(TokenKind.String, "``", line));
                index = ScanTemplate(text, index + 1, ref line, templateDepths, braceDepth);
                continue;
            }
            if (char.IsLetter(character) || character is '_' or '$' || character > 127)
            {
                var start = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$' || text[index] > 127))
                    index++;
                tokens.Add(new Token(TokenKind.Word, text[start..index], line));
                continue;
            }
            if (char.IsDigit(character) || character == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1]))
            {
                var start = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '.' or '_')) index++;
                tokens.Add(new Token(TokenKind.Number, text[start..index], line));
                continue;
            }
            if (character == '/' && StartsRegex(tokens))
            {
                index = SkipRegex(text, index);
                tokens.Add(new Token(TokenKind.Regex, "/re/", line));
                continue;
            }
            var op = Operators.FirstOrDefault(candidate => string.CompareOrdinal(text, index, candidate, 0, candidate.Length) == 0);
            // `a?.5:1` is a conditional followed by a number, not optional chaining.
            if (op == "?." && index + 2 < text.Length && char.IsDigit(text[index + 2])) op = null;
            if (op is not null)
            {
                tokens.Add(new Token(TokenKind.Punct, op, line));
                index += op.Length;
                continue;
            }
            if (character == '{') braceDepth++;
            else if (character == '}') braceDepth--;
            tokens.Add(new Token(TokenKind.Punct, character.ToString(), line));
            index++;
        }
        return tokens;
    }

    private static bool StartsRegex(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;
        var previous = tokens[^1];
        return previous.Kind switch
        {
            TokenKind.Word => RegexPrefixKeywords.Contains(previous.Text),
            TokenKind.Punct => previous.Text is not (")" or "]" or "}" or "++" or "--"),
            _ => false,
        };
    }

    private static int SkipQuoted(string text, int index, char quote, ref int line)
    {
        index++;
        while (index < text.Length && text[index] != quote)
        {
            if (text[index] == '\\') index++;
            else if (text[index] == '\n') { line++; break; }
            index++;
        }
        return Math.Min(text.Length, index + 1);
    }

    /// <summary>Scans template text up to its end or to a <c>${</c>, which resumes normal tokenizing.</summary>
    private static int ScanTemplate(string text, int index, ref int line, Stack<int> templateDepths, int braceDepth)
    {
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\\') { index += 2; continue; }
            if (character == '\n') line++;
            if (character == '`') return index + 1;
            if (character == '$' && index + 1 < text.Length && text[index + 1] == '{')
            {
                templateDepths.Push(braceDepth);
                return index + 2;
            }
            index++;
        }
        return index;
    }

    private static int SkipRegex(string text, int index)
    {
        index++;
        var inClass = false;
        while (index < text.Length && text[index] != '\n')
        {
            var character = text[index];
            if (character == '\\') { index += 2; continue; }
            if (character == '[') inClass = true;
            else if (character == ']') inClass = false;
            else if (character == '/' && !inClass) { index++; break; }
            index++;
        }
        while (index < text.Length && char.IsLetter(text[index])) index++;
        return index;
    }

    private static int Count(string text, int start, int end, char character)
    {
        var count = 0;
        for (var index = start; index < end && index < text.Length; index++)
            if (text[index] == character) count++;
        return count;
    }
}
