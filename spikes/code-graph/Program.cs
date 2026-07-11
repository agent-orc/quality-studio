using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeGraphSpike;

internal static class Program
{
    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            var solutionPath = ResolveSolutionPath(args);

            // MSBuild assemblies must be registered before MSBuildWorkspace is created.
            MSBuildLocator.RegisterDefaults();

            using var workspace = MSBuildWorkspace.Create();
            var workspaceDiagnostics = new ConcurrentQueue<WorkspaceDiagnostic>();
            workspace.RegisterWorkspaceFailedHandler(eventArgs => workspaceDiagnostics.Enqueue(eventArgs.Diagnostic));

            var totalTimer = Stopwatch.StartNew();
            var loadTimer = Stopwatch.StartNew();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            loadTimer.Stop();

            var analysisTimer = Stopwatch.StartNew();
            var graph = await BuildGraphAsync(solution);
            analysisTimer.Stop();

            var diagnostics = workspaceDiagnostics.ToArray();
            PrintGraph(solutionPath, solution, graph, diagnostics, loadTimer.Elapsed, analysisTimer.Elapsed);

            totalTimer.Stop();
            using var process = Process.GetCurrentProcess();
            Console.WriteLine();
            Console.WriteLine("METRICS");
            Console.WriteLine(
                $"  elapsed_ms={totalTimer.ElapsedMilliseconds} " +
                $"load_ms={loadTimer.ElapsedMilliseconds} " +
                $"analysis_ms={analysisTimer.ElapsedMilliseconds} " +
                $"managed_mb={ToMiB(GC.GetTotalMemory(forceFullCollection: false)):F1} " +
                $"working_set_mb={ToMiB(process.WorkingSet64):F1} " +
                $"peak_working_set_mb={ToMiB(process.PeakWorkingSet64):F1}");

            return diagnostics.Any(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure) ? 1 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"code-graph spike failed: {exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string ResolveSolutionPath(string[] args)
    {
        if (args.Length > 1)
        {
            throw new ArgumentException("Usage: dotnet run --project spikes/code-graph -- [solution.sln|solution.slnx]");
        }

        string solutionPath;
        if (args.Length == 1)
        {
            solutionPath = Path.GetFullPath(args[0]);
        }
        else
        {
            var candidates = Directory
                .EnumerateFiles(Directory.GetCurrentDirectory(), "*.sln*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            solutionPath = candidates.Length switch
            {
                1 => Path.GetFullPath(candidates[0]),
                0 => throw new FileNotFoundException("No .sln or .slnx file found in the current directory."),
                _ => throw new InvalidOperationException("Multiple solution files found; pass one explicitly."),
            };
        }

        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException("Solution file not found.", solutionPath);
        }

        return solutionPath;
    }

    private static async Task<CodeGraph> BuildGraphAsync(Solution solution)
    {
        var typesByKey = new Dictionary<string, SourceType>(StringComparer.Ordinal);
        var typesBySymbolKey = new Dictionary<string, List<SourceType>>(StringComparer.Ordinal);
        var compilations = new Dictionary<ProjectId, Compilation>();
        var sourceDocuments = new Dictionary<ProjectId, Document[]>();

        foreach (var project in solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                throw new InvalidOperationException($"Project '{project.Name}' did not produce a compilation.");
            }

            compilations.Add(project.Id, compilation);
            var projectSourceDocuments = project.Documents
                .Where(document => IsOrdinarySourceDocument(project, document))
                .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            sourceDocuments.Add(project.Id, projectSourceDocuments);

            var documentTrees = new HashSet<SyntaxTree>();
            foreach (var document in projectSourceDocuments)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree is not null)
                {
                    documentTrees.Add(syntaxTree);
                }
            }

            CollectNamespaceTypes(
                compilation.Assembly.GlobalNamespace,
                project,
                documentTrees,
                typesByKey,
                typesBySymbolKey);
        }

        var referenceEdges = new HashSet<TypeReferenceEdge>();
        foreach (var project in solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            if (!compilations.TryGetValue(project.Id, out var compilation))
            {
                continue;
            }

            foreach (var document in sourceDocuments[project.Id])
            {
                var root = await document.GetSyntaxRootAsync();
                var semanticModel = await document.GetSemanticModelAsync();
                if (root is null || semanticModel is null)
                {
                    continue;
                }

                var declaredTypeCache = new Dictionary<SyntaxNode, INamedTypeSymbol?>();
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var sourceType = GetEnclosingSourceType(semanticModel, name, declaredTypeCache);
                    if (sourceType is null
                        || !TryGetSourceType(typesBySymbolKey, sourceType, project.Id, out var source))
                    {
                        continue;
                    }

                    var symbolInfo = semanticModel.GetSymbolInfo(name);
                    var targetSymbol = symbolInfo.Symbol
                        ?? symbolInfo.CandidateSymbols.FirstOrDefault()
                        ?? semanticModel.GetTypeInfo(name).Type;
                    var targetType = GetReferencedType(targetSymbol);

                    if (targetType is null)
                    {
                        continue;
                    }

                    var target = ResolveTargetType(
                        typesBySymbolKey,
                        targetType,
                        project,
                        compilation);
                    if (target is null || source.Key == target.Key)
                    {
                        continue;
                    }

                    referenceEdges.Add(new TypeReferenceEdge(source.Key, target.Key));
                }
            }
        }

        return new CodeGraph(typesByKey, referenceEdges);
    }

    private static INamedTypeSymbol? GetEnclosingSourceType(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        Dictionary<SyntaxNode, INamedTypeSymbol?> declaredTypeCache)
    {
        var containingTypeDeclaration = name.Ancestors().FirstOrDefault(node =>
            node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

        INamedTypeSymbol? declaredType = null;
        if (containingTypeDeclaration is not null
            && !declaredTypeCache.TryGetValue(containingTypeDeclaration, out declaredType))
        {
            declaredType = containingTypeDeclaration switch
            {
                BaseTypeDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration) as INamedTypeSymbol,
                DelegateDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration) as INamedTypeSymbol,
                _ => null,
            };
            declaredTypeCache.Add(containingTypeDeclaration, declaredType);
        }
        if (declaredType is not null)
        {
            return declaredType;
        }

        var enclosingSymbol = semanticModel.GetEnclosingSymbol(name.SpanStart);
        return enclosingSymbol as INamedTypeSymbol ?? enclosingSymbol?.ContainingType;
    }

    private static bool TryGetSourceType(
        IReadOnlyDictionary<string, List<SourceType>> typesBySymbolKey,
        INamedTypeSymbol symbol,
        ProjectId projectId,
        out SourceType sourceType)
    {
        if (typesBySymbolKey.TryGetValue(CreateSymbolTypeKey(symbol), out var candidates))
        {
            var candidate = candidates.SingleOrDefault(type => type.ProjectId == projectId);
            if (candidate is not null)
            {
                sourceType = candidate;
                return true;
            }
        }

        sourceType = null!;
        return false;
    }

    private static SourceType? ResolveTargetType(
        IReadOnlyDictionary<string, List<SourceType>> typesBySymbolKey,
        INamedTypeSymbol symbol,
        Project currentProject,
        Compilation currentCompilation)
    {
        var symbolKey = CreateSymbolTypeKey(symbol);
        if (!typesBySymbolKey.TryGetValue(symbolKey, out var candidates))
        {
            return null;
        }

        if (SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, currentCompilation.Assembly))
        {
            return candidates.SingleOrDefault(type => type.ProjectId == currentProject.Id);
        }

        var referencedProjectIds = currentProject.ProjectReferences
            .Select(reference => reference.ProjectId)
            .ToHashSet();
        var referencedCandidates = candidates
            .Where(candidate => referencedProjectIds.Contains(candidate.ProjectId))
            .ToArray();
        if (referencedCandidates.Length == 1)
        {
            return referencedCandidates[0];
        }

        if (referencedCandidates.Length == 0)
        {
            // A binary/package can share identity with a loaded source project.
            // Without an evaluated project reference, do not invent provenance.
            return null;
        }

        throw new InvalidOperationException(
            $"Type identity '{symbolKey}' is ambiguous between projects: " +
            string.Join(", ", candidates.Select(candidate => candidate.ProjectName).Order(StringComparer.Ordinal)));
    }

    private static bool IsOrdinarySourceDocument(Project project, Document document)
    {
        if (string.IsNullOrWhiteSpace(project.FilePath)
            || string.IsNullOrWhiteSpace(document.FilePath))
        {
            return false;
        }

        var projectDirectory = Path.GetDirectoryName(project.FilePath)
            ?? throw new InvalidOperationException($"Project '{project.Name}' has no directory.");
        var relativePath = Path.GetRelativePath(projectDirectory, document.FilePath);
        return !relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static void CollectNamespaceTypes(
        INamespaceSymbol namespaceSymbol,
        Project project,
        IReadOnlySet<SyntaxTree> documentTrees,
        Dictionary<string, SourceType> typesByKey,
        Dictionary<string, List<SourceType>> typesBySymbolKey)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            CollectType(type, project, documentTrees, typesByKey, typesBySymbolKey);
        }

        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            CollectNamespaceTypes(
                childNamespace,
                project,
                documentTrees,
                typesByKey,
                typesBySymbolKey);
        }
    }

    private static void CollectType(
        INamedTypeSymbol type,
        Project project,
        IReadOnlySet<SyntaxTree> documentTrees,
        Dictionary<string, SourceType> typesByKey,
        Dictionary<string, List<SourceType>> typesBySymbolKey)
    {
        if (type.DeclaringSyntaxReferences.Any(reference => documentTrees.Contains(reference.SyntaxTree)))
        {
            var originalType = type.OriginalDefinition;
            var symbolKey = CreateSymbolTypeKey(originalType);
            var key = $"{project.Id.Id:N}|{symbolKey}";
            var sourceType = new SourceType(
                key,
                symbolKey,
                project.Id,
                project.Name,
                originalType.ContainingNamespace.IsGlobalNamespace
                    ? string.Empty
                    : originalType.ContainingNamespace.ToDisplayString(),
                originalType.ToDisplayString(TypeDisplayFormat),
                FormatLocalTypeName(originalType),
                FormatTypeKind(originalType));
            typesByKey.Add(key, sourceType);

            if (!typesBySymbolKey.TryGetValue(symbolKey, out var candidates))
            {
                candidates = [];
                typesBySymbolKey.Add(symbolKey, candidates);
            }

            candidates.Add(sourceType);
        }

        foreach (var nestedType in type.GetTypeMembers())
        {
            CollectType(nestedType, project, documentTrees, typesByKey, typesBySymbolKey);
        }
    }

    private static string CreateSymbolTypeKey(INamedTypeSymbol type)
    {
        var originalType = type.OriginalDefinition;
        var documentationId = originalType.GetDocumentationCommentId()
            ?? originalType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{originalType.ContainingAssembly.Identity}|{documentationId}";
    }

    private static INamedTypeSymbol? GetReferencedType(ISymbol? symbol)
    {
        return symbol switch
        {
            IAliasSymbol alias => GetReferencedType(alias.Target),
            INamedTypeSymbol namedType => namedType.OriginalDefinition,
            IMethodSymbol method => (method.ReducedFrom ?? method).ContainingType?.OriginalDefinition,
            { ContainingType: not null } member => member.ContainingType.OriginalDefinition,
            _ => null,
        };
    }

    private static string FormatLocalTypeName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var typeParameters = current.TypeParameters.Length == 0
                ? string.Empty
                : $"<{string.Join(", ", current.TypeParameters.Select(parameter => parameter.Name))}>";
            names.Push($"{current.Name}{typeParameters}");
        }

        return string.Join('.', names);
    }

    private static string FormatTypeKind(INamedTypeSymbol type) => type.IsRecord
        ? type.TypeKind == TypeKind.Struct ? "RecordStruct" : "Record"
        : type.TypeKind.ToString();

    private static void PrintGraph(
        string solutionPath,
        Solution solution,
        CodeGraph graph,
        IReadOnlyCollection<WorkspaceDiagnostic> workspaceDiagnostics,
        TimeSpan loadDuration,
        TimeSpan analysisDuration)
    {
        Console.WriteLine("CODE GRAPH");
        Console.WriteLine($"Solution: {solutionPath}");
        var namespaceCount = graph.TypesByKey.Values
            .Select(type => (type.ProjectId, type.Namespace))
            .Distinct()
            .Count();
        var projectReferenceCount = solution.Projects.Sum(project => project.ProjectReferences.Count());
        Console.WriteLine(
            $"Summary: {solution.ProjectIds.Count} projects, " +
            $"{namespaceCount} namespaces, " +
            $"{graph.TypesByKey.Count} source types, " +
            $"{projectReferenceCount} project-reference edges, " +
            $"{graph.ReferenceEdges.Count} source-type reference edges");
        Console.WriteLine();
        Console.WriteLine("TREE");

        foreach (var project in solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"PROJECT {project.Name} [{project.Language}]");
            var projectTypes = graph.TypesByKey.Values
                .Where(type => type.ProjectId == project.Id)
                .OrderBy(type => type.Namespace, StringComparer.Ordinal)
                .ThenBy(type => type.DisplayName, StringComparer.Ordinal)
                .ToArray();

            var namespaceTree = BuildNamespaceTree(projectTypes);
            PrintNamespaceTree(namespaceTree, indent: 1, printCurrentNamespace: false);
        }

        Console.WriteLine();
        Console.WriteLine("REFERENCE EDGES");
        foreach (var project in solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            foreach (var reference in project.ProjectReferences
                .Select(reference => solution.GetProject(reference.ProjectId))
                .Where(referencedProject => referencedProject is not null)
                .OrderBy(referencedProject => referencedProject!.Name, StringComparer.Ordinal))
            {
                Console.WriteLine($"PROJECT_REF {project.Name} -> {reference!.Name}");
            }
        }

        foreach (var edge in graph.ReferenceEdges
            .Select(edge => (From: graph.TypesByKey[edge.FromKey], To: graph.TypesByKey[edge.ToKey]))
            .OrderBy(edge => edge.From.ProjectName, StringComparer.Ordinal)
            .ThenBy(edge => edge.From.DisplayName, StringComparer.Ordinal)
            .ThenBy(edge => edge.To.ProjectName, StringComparer.Ordinal)
            .ThenBy(edge => edge.To.DisplayName, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"TYPE_REF {edge.From.ProjectName}::{edge.From.DisplayName} " +
                $"-> {edge.To.ProjectName}::{edge.To.DisplayName}");
        }

        Console.WriteLine();
        Console.WriteLine("WORKSPACE DIAGNOSTICS");
        if (workspaceDiagnostics.Count == 0)
        {
            Console.WriteLine("  none");
        }
        else
        {
            foreach (var diagnostic in workspaceDiagnostics)
            {
                Console.WriteLine($"  {diagnostic.Kind}: {diagnostic.Message}");
            }
        }

        Console.WriteLine(
            $"  load_ms={loadDuration.TotalMilliseconds:F0} " +
            $"analysis_ms={analysisDuration.TotalMilliseconds:F0}");
    }

    private static NamespaceNode BuildNamespaceTree(IEnumerable<SourceType> types)
    {
        var root = new NamespaceNode(string.Empty);
        foreach (var type in types)
        {
            var current = root;
            foreach (var segment in type.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new NamespaceNode(segment);
                    current.Children.Add(segment, child);
                }

                current = child;
            }

            current.Types.Add(type);
        }

        return root;
    }

    private static void PrintNamespaceTree(NamespaceNode node, int indent, bool printCurrentNamespace)
    {
        var contentsIndent = indent;
        if (printCurrentNamespace)
        {
            Console.WriteLine($"{new string(' ', indent * 2)}NAMESPACE {node.Name}");
            contentsIndent++;
        }

        if (node.Types.Count > 0)
        {
            var typeIndent = contentsIndent;
            if (!printCurrentNamespace)
            {
                Console.WriteLine($"{new string(' ', indent * 2)}NAMESPACE <global>");
                typeIndent++;
            }

            foreach (var type in node.Types.OrderBy(type => type.DisplayName, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"{new string(' ', typeIndent * 2)}TYPE {type.LocalName} [{type.Kind}]");
            }
        }

        foreach (var child in node.Children.Values.OrderBy(child => child.Name, StringComparer.Ordinal))
        {
            PrintNamespaceTree(child, contentsIndent, printCurrentNamespace: true);
        }
    }

    private static double ToMiB(long bytes) => bytes / 1024d / 1024d;

    private sealed record SourceType(
        string Key,
        string SymbolKey,
        ProjectId ProjectId,
        string ProjectName,
        string Namespace,
        string DisplayName,
        string LocalName,
        string Kind);

    private sealed record TypeReferenceEdge(string FromKey, string ToKey);

    private sealed record CodeGraph(
        IReadOnlyDictionary<string, SourceType> TypesByKey,
        IReadOnlySet<TypeReferenceEdge> ReferenceEdges);

    private sealed class NamespaceNode(string name)
    {
        public string Name { get; } = name;

        public Dictionary<string, NamespaceNode> Children { get; } = new(StringComparer.Ordinal);

        public List<SourceType> Types { get; } = [];
    }
}
