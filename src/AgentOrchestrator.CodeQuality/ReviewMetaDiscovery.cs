using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Discovers review-meta sidecars and attaches matching documents by unit ID.</summary>
public static class ReviewMetaDiscovery
{
    private static readonly EnumerationOptions ConfinedEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static void AttachDiscovered(string repositoryPath, IEnumerable<HierarchyNode> projects)
    {
        var root = Path.GetFullPath(repositoryPath);
        var nodes = Flatten(projects).ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                     .Where(path => path.Contains(".review-meta.", StringComparison.Ordinal)))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var document = json.RootElement;
            if (!document.TryGetProperty("unit", out var unit) ||
                !unit.TryGetProperty("id", out var idProperty) ||
                !document.TryGetProperty("kind", out var kindProperty))
            {
                continue;
            }

            var unitId = idProperty.GetString();
            if (unitId is null || !nodes.TryGetValue(unitId, out var node) ||
                !Enum.TryParse<ReviewKind>(kindProperty.GetString(), true, out var kind))
            {
                continue;
            }

            node.Attach(new AttachedReviewMetaDocument(
                unitId,
                kind,
                IsStale(root, node, document) ? ReviewState.Stale : ReviewState.Current,
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                document.GetRawText()));
        }
    }

    private static bool IsStale(string root, HierarchyNode node, JsonElement document)
    {
        if (!document.TryGetProperty("subjectInputs", out var inputs))
        {
            return false;
        }

        foreach (var input in inputs.EnumerateArray())
        {
            var selector = input.GetProperty("selector").GetString();
            if (selector == "aggregate-members")
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
                if (!StringComparer.Ordinal.Equals(input.GetProperty("contentHash").GetString(),
                        ReviewSubjectHasher.ComputeAggregateMembersHash(members))) return true;
                continue;
            }
            if (selector is not ("file" or "aggregate-control"))
            {
                continue;
            }

            var path = Path.GetFullPath(input.GetProperty("path").GetString()!, root);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                return true;
            }

            var expected = input.GetProperty("contentHash").GetString();
            if (!StringComparer.Ordinal.Equals(expected, HashNormalizedText(path)))
            {
                return true;
            }
        }

        return false;
    }

    private static string HashNormalizedText(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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
