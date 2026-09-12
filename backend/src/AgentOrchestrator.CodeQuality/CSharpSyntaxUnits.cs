using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentOrchestrator.CodeQuality;

/// <summary>One derived C# function unit: a display name and its syntactic documentation ID.</summary>
internal sealed record CSharpFunctionUnit(string Name, string DocumentationId);

/// <summary>The namespaces a source file contributes to and the function units it declares.</summary>
internal sealed record CSharpSourceUnits(
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<CSharpFunctionUnit> Functions);

/// <summary>
/// Derives namespace and function units from a C# syntax tree. Only the parser runs: there is no
/// compilation, no reference resolution, and no semantic model, so derivation is deterministic and
/// works offline on a single file. The consequences are documented in
/// <c>docs/hierarchy-derivation.md</c> — most importantly, parameter and return types keep the text
/// written in the source, so `using` aliases and imported names are not expanded and the resulting
/// identifier is a syntactic approximation of a Roslyn documentation ID.
/// </summary>
internal static class CSharpSyntaxUnits
{
    /// <summary>The unit name for types that are declared outside any namespace.</summary>
    public const string GlobalNamespace = "<global>";

    /// <summary>Identity-tuple literal for a syntactically derived function documentation ID.</summary>
    public const string FunctionKey = "csharp-syntax-doc-id-v1";

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    public static CSharpSourceUnits Parse(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, ParseOptions).GetCompilationUnitRoot();
        var namespaces = new List<string>();
        var functions = new List<CSharpFunctionUnit>();
        var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var seenFunctions = new HashSet<string>(StringComparer.Ordinal);

        // Method bodies contain no reviewable units of their own: local functions stay locations on
        // their containing member and lambdas are not units at all.
        foreach (var node in root.DescendantNodes(descendant =>
                     descendant is not (BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax)))
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    AddNamespace(ContainingNamespace(node));
                    break;
                case BaseNamespaceDeclarationSyntax declaration
                    when !declaration.Members.OfType<BaseNamespaceDeclarationSyntax>().Any():
                    AddNamespace(FullNamespace(declaration));
                    break;
            }

            var function = DeriveFunction(node);
            if (function is not null && seenFunctions.Add(function.DocumentationId))
            {
                functions.Add(function);
            }
        }

        if (root.Members.OfType<GlobalStatementSyntax>().Any() || namespaces.Count == 0)
        {
            AddNamespace(GlobalNamespace);
        }

        namespaces.Sort(StringComparer.Ordinal);
        return new CSharpSourceUnits(namespaces, functions);

        void AddNamespace(string name)
        {
            if (seenNamespaces.Add(name))
            {
                namespaces.Add(name);
            }
        }
    }

    private static CSharpFunctionUnit? DeriveFunction(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => Unit(
            method,
            method.Identifier.Text + MethodArity(method.TypeParameterList),
            Parameters(method.ParameterList)),
        ConstructorDeclarationSyntax constructor => Unit(
            constructor,
            constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? "#cctor" : "#ctor",
            Parameters(constructor.ParameterList)),
        DestructorDeclarationSyntax destructor => Unit(destructor, "Finalize", []),
        OperatorDeclarationSyntax op => Unit(op, OperatorName(op), Parameters(op.ParameterList)),
        ConversionOperatorDeclarationSyntax conversion => Unit(
            conversion,
            ConversionName(conversion),
            Parameters(conversion.ParameterList),
            "~" + TypeText(conversion.Type)),
        AccessorDeclarationSyntax accessor => DeriveAccessor(accessor),
        PropertyDeclarationSyntax { ExpressionBody: not null } property => Unit(
            property, "get_" + property.Identifier.Text, []),
        IndexerDeclarationSyntax { ExpressionBody: not null } indexer => Unit(
            indexer, "get_Item", Parameters(indexer.ParameterList)),
        _ => null,
    };

    private static CSharpFunctionUnit? DeriveAccessor(AccessorDeclarationSyntax accessor)
    {
        if (accessor.Parent?.Parent is not BasePropertyDeclarationSyntax owner)
        {
            return null;
        }

        var (name, indexParameters) = owner switch
        {
            PropertyDeclarationSyntax property => (property.Identifier.Text, Array.Empty<string>()),
            // An `IndexerNameAttribute` can rename an indexer; that attribute is not evaluated here.
            IndexerDeclarationSyntax indexer => ("Item", Parameters(indexer.ParameterList)),
            EventDeclarationSyntax @event => (@event.Identifier.Text, Array.Empty<string>()),
            _ => (string.Empty, Array.Empty<string>()),
        };
        if (name.Length == 0)
        {
            return null;
        }

        var valueType = TypeText(owner.Type);
        return accessor.Keyword.Kind() switch
        {
            SyntaxKind.GetKeyword => Unit(accessor, "get_" + name, indexParameters),
            // `init` shares the `set_` metadata name with a setter; a member never declares both.
            SyntaxKind.SetKeyword or SyntaxKind.InitKeyword => Unit(
                accessor, "set_" + name, [.. indexParameters, valueType]),
            SyntaxKind.AddKeyword => Unit(accessor, "add_" + name, [valueType]),
            SyntaxKind.RemoveKeyword => Unit(accessor, "remove_" + name, [valueType]),
            _ => null,
        };
    }

    private static CSharpFunctionUnit Unit(
        SyntaxNode node, string memberName, IReadOnlyList<string> parameters, string suffix = "")
    {
        var container = ContainingSymbolPath(node);
        var qualified = container.Length == 0 ? memberName : container + "." + memberName;
        var signature = parameters.Count == 0 ? string.Empty : "(" + string.Join(',', parameters) + ")";
        return new CSharpFunctionUnit(
            memberName + "(" + string.Join(", ", parameters) + ")",
            "M:" + qualified + signature + suffix);
    }

    /// <summary>Namespace and nested type chain of a declaration, in documentation-ID spelling.</summary>
    private static string ContainingSymbolPath(SyntaxNode node)
    {
        var types = node.Ancestors().OfType<TypeDeclarationSyntax>()
            .Select(type => type.Identifier.Text + TypeArity(type.TypeParameterList))
            .Reverse();
        var containingNamespace = ContainingNamespace(node);
        var segments = containingNamespace == GlobalNamespace
            ? types
            : new[] { containingNamespace }.Concat(types);
        return string.Join('.', segments);
    }

    private static string ContainingNamespace(SyntaxNode node)
    {
        var names = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(declaration => TypeText(declaration.Name))
            .Reverse()
            .ToArray();
        return names.Length == 0 ? GlobalNamespace : string.Join('.', names);
    }

    private static string FullNamespace(BaseNamespaceDeclarationSyntax declaration)
    {
        var parent = ContainingNamespace(declaration);
        var name = TypeText(declaration.Name);
        return parent == GlobalNamespace ? name : parent + "." + name;
    }

    private static string[] Parameters(BaseParameterListSyntax? list) =>
        list is null
            ? []
            : list.Parameters.Select(parameter =>
                TypeText(parameter.Type) + (IsByReference(parameter) ? "@" : string.Empty)).ToArray();

    private static bool IsByReference(ParameterSyntax parameter) =>
        parameter.Modifiers.Any(modifier => modifier.Kind() is
            SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword);

    private static string TypeArity(TypeParameterListSyntax? list) =>
        list is null or { Parameters.Count: 0 } ? string.Empty : "`" + list.Parameters.Count;

    private static string MethodArity(TypeParameterListSyntax? list) =>
        list is null or { Parameters.Count: 0 } ? string.Empty : "``" + list.Parameters.Count;

    /// <summary>Source type text with insignificant whitespace removed; nothing is resolved.</summary>
    private static string TypeText(SyntaxNode? type)
    {
        if (type is null)
        {
            return "?";
        }

        var builder = new StringBuilder();
        foreach (var character in type.ToString())
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? "?" : builder.ToString();
    }

    private static string ConversionName(ConversionOperatorDeclarationSyntax conversion) =>
        Checked(conversion.CheckedKeyword,
            conversion.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword) ? "Implicit" : "Explicit");

    private static string OperatorName(OperatorDeclarationSyntax op)
    {
        var unary = op.ParameterList.Parameters.Count == 1;
        var name = op.OperatorToken.Kind() switch
        {
            SyntaxKind.PlusToken => unary ? "UnaryPlus" : "Addition",
            SyntaxKind.MinusToken => unary ? "UnaryNegation" : "Subtraction",
            SyntaxKind.ExclamationToken => "LogicalNot",
            SyntaxKind.TildeToken => "OnesComplement",
            SyntaxKind.PlusPlusToken => "Increment",
            SyntaxKind.MinusMinusToken => "Decrement",
            SyntaxKind.TrueKeyword => "True",
            SyntaxKind.FalseKeyword => "False",
            SyntaxKind.AsteriskToken => "Multiply",
            SyntaxKind.SlashToken => "Division",
            SyntaxKind.PercentToken => "Modulus",
            SyntaxKind.AmpersandToken => "BitwiseAnd",
            SyntaxKind.BarToken => "BitwiseOr",
            SyntaxKind.CaretToken => "ExclusiveOr",
            SyntaxKind.LessThanLessThanToken => "LeftShift",
            SyntaxKind.GreaterThanGreaterThanToken => "RightShift",
            SyntaxKind.GreaterThanGreaterThanGreaterThanToken => "UnsignedRightShift",
            SyntaxKind.EqualsEqualsToken => "Equality",
            SyntaxKind.ExclamationEqualsToken => "Inequality",
            SyntaxKind.LessThanToken => "LessThan",
            SyntaxKind.GreaterThanToken => "GreaterThan",
            SyntaxKind.LessThanEqualsToken => "LessThanOrEqual",
            SyntaxKind.GreaterThanEqualsToken => "GreaterThanOrEqual",
            var kind => kind.ToString(),
        };
        return Checked(op.CheckedKeyword, name);
    }

    private static string Checked(SyntaxToken keyword, string name) =>
        keyword.IsKind(SyntaxKind.CheckedKeyword) ? "op_Checked" + name : "op_" + name;
}
