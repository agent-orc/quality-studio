using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Discovers review-meta sidecars and attaches matching documents by unit ID.</summary>
public static class ReviewMetaDiscovery
{
    private static readonly EnumerationOptions ConfinedEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static void AttachDiscovered(
        string repositoryPath,
        IEnumerable<HierarchyNode> projects,
        InputResolver? inputResolver = null,
        string? globalInputsDirectory = null,
        int inputBudgetCharacters = InputResolver.DefaultBudgetCharacters)
    {
        var root = Path.GetFullPath(repositoryPath);
        // A file contributing to several namespaces is aliased below each of them while remaining
        // one canonical unit, so the same node can be reached more than once during the walk.
        var nodes = Flatten(projects)
            .DistinctBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                     .Where(path => path.Contains(".review-meta.", StringComparison.Ordinal)))
        {
            // A sidecar that cannot be trusted attaches to no unit; the reader reports it, and the
            // unit stays "not reviewed" instead of inheriting a grade from an unvalidated file.
            if (!ReviewMetaReader.TryLoad(path, out var sidecar, out _)) continue;
            var document = sidecar.Document;
            if (!nodes.TryGetValue(document.Unit.Id, out var node)) continue;

            node.Attach(new AttachedReviewMetaDocument(
                document.Unit.Id,
                document.Kind,
                DetermineState(root, node, document, inputResolver ?? new InputResolver(), globalInputsDirectory, inputBudgetCharacters),
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                sidecar.Json));
        }
    }

    private static ReviewState DetermineState(
        string root,
        HierarchyNode node,
        ReviewMetaDocument document,
        InputResolver inputResolver,
        string? globalInputsDirectory,
        int inputBudgetCharacters)
    {
        foreach (var input in document.SubjectInputs)
        {
            if (input.Selector == "aggregate-members")
            {
                var members = Flatten([node]).Where(candidate => candidate.Level == ReviewLevel.File)
                    .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .Select(candidate =>
                    {
                        var contentHash = HashNormalizedText(Path.GetFullPath(candidate.Path, root));
                        var subjectHash = "sha256:" + ReviewSubjectHasher.ComputeManifestHash(candidate.Id,
                            [new SubjectInputHash(candidate.Path, "file", contentHash)]);
                        return new AggregateMemberHash(candidate.Id, candidate.Path, subjectHash);
                    }).ToArray();
                if (!StringComparer.Ordinal.Equals(input.ContentHash,
                        ReviewSubjectHasher.ComputeAggregateMembersHash(members, node.Exclusions))) return ReviewState.Stale;
                continue;
            }
            if (input.Selector is not ("file" or "aggregate-control"))
            {
                continue;
            }

            var path = Path.GetFullPath(input.Path, root);
            if (!IsConfinedFile(root, path) ||
                !StringComparer.Ordinal.Equals(input.ContentHash, HashNormalizedText(path)))
            {
                return ReviewState.Stale;
            }
        }

        var kind = document.Kind.ToString().ToLowerInvariant();
        var level = document.Unit.Level;
        // The sidecar records the adapter the review ran under; resolving with any other value would
        // select a different rule set and report every unit as policy drift.
        var adapter = document.Unit.Adapter.ToString().ToLowerInvariant();
        var resolved = inputResolver.Resolve(root, kind, level, globalInputsDirectory, inputBudgetCharacters, adapter);
        return StringComparer.Ordinal.Equals(
            document.ReviewInputs.EffectiveHash.Value, resolved.EffectiveHash(ReviewPromptBuilder.TemplateHash(level, kind)))
            ? ReviewState.Current
            : ReviewState.PolicyDrift;
    }

    private static string HashNormalizedText(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool IsConfinedFile(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) || !File.Exists(normalizedPath))
            return false;
        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }
}
