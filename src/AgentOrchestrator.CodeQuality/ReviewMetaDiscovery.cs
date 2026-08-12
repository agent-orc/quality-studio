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

    public static void AttachDiscovered(
        string repositoryPath,
        IEnumerable<HierarchyNode> projects,
        InputResolver? inputResolver = null,
        string? globalInputsDirectory = null,
        int inputBudgetCharacters = InputResolver.DefaultBudgetCharacters)
    {
        var root = Path.GetFullPath(repositoryPath);
        var limits = ReviewContentLimits.Default;
        var nodes = Flatten(projects).ToDictionary(node => node.Id, StringComparer.Ordinal);
        long aggregateBytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                     .Where(path => path.Contains(".review-meta.", StringComparison.Ordinal))
                     .Take(limits.MaxSidecarCount))
        {
            try
            {
                aggregateBytes = checked(aggregateBytes +
                    BoundedRepositoryFile.Length(root, path, limits.MaxSidecarBytes));
                if (aggregateBytes > limits.MaxSidecarAggregateBytes) continue;
                var content = BoundedRepositoryFile.ReadAllText(root, path, limits.MaxSidecarBytes);
                _ = ReviewMetaJson.Deserialize(content);
                using var json = JsonDocument.Parse(content);
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
                    DetermineState(root, node, document, inputResolver ?? new InputResolver(), globalInputsDirectory, inputBudgetCharacters),
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    document.GetRawText()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              ArgumentException or ReviewContentLimitException or InvalidOperationException or
                                              OverflowException)
            {
                // Invalid repository-owned sidecars are quarantined by the API index and never enter the hierarchy.
            }
        }
    }

    private static ReviewState DetermineState(
        string root,
        HierarchyNode node,
        JsonElement document,
        InputResolver inputResolver,
        string? globalInputsDirectory,
        int inputBudgetCharacters)
    {
        if (!document.TryGetProperty("subjectInputs", out var inputs))
        {
            return ReviewState.Current;
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
                        var contentHash = HashNormalizedText(root, Path.GetFullPath(candidate.Path, root));
                        var subjectHash = "sha256:" + ReviewSubjectHasher.ComputeManifestHash(candidate.Id,
                            [new SubjectInputHash(candidate.Path, "file", contentHash)]);
                        return new AggregateMemberHash(candidate.Id, candidate.Path, subjectHash);
                    }).ToArray();
                if (!StringComparer.Ordinal.Equals(input.GetProperty("contentHash").GetString(),
                        ReviewSubjectHasher.ComputeAggregateMembersHash(members, node.Exclusions))) return ReviewState.Stale;
                continue;
            }
            if (selector is not ("file" or "aggregate-control"))
            {
                continue;
            }

            var path = Path.GetFullPath(input.GetProperty("path").GetString()!, root);
            if (!IsConfinedFile(root, path))
            {
                return ReviewState.Stale;
            }

            var expected = input.GetProperty("contentHash").GetString();
            if (!StringComparer.Ordinal.Equals(expected, HashNormalizedText(root, path)))
            {
                return ReviewState.Stale;
            }
        }

        if (!document.TryGetProperty("reviewInputs", out var reviewInputs) ||
            !reviewInputs.TryGetProperty("effectiveHash", out var effectiveHash) ||
            !effectiveHash.TryGetProperty("value", out var expectedHash)) return ReviewState.Current;
        var kind = document.GetProperty("kind").GetString()!;
        var levelText = document.GetProperty("unit").GetProperty("level").GetString()!;
        if (!Enum.TryParse<ReviewLevel>(levelText, true, out var level)) return ReviewState.Current;
        var resolved = inputResolver.Resolve(root, kind, level, globalInputsDirectory, inputBudgetCharacters);
        var currentHash = resolved.EffectiveHash(ReviewPromptBuilder.TemplateHash(kind));
        return StringComparer.Ordinal.Equals(expectedHash.GetString(), currentHash)
            ? ReviewState.Current
            : ReviewState.PolicyDrift;
    }

    private static string HashNormalizedText(string root, string path)
    {
        var text = BoundedRepositoryFile.ReadAllText(root, path, ReviewContentLimits.Default.MaxFileBytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
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
