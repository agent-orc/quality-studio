using System.Collections.ObjectModel;

namespace AgentOrchestrator.CodeQuality;

/// <summary>A derived source unit and its review metadata.</summary>
public sealed class HierarchyNode
{
    private readonly List<HierarchyNode> children = [];
    private readonly Dictionary<ReviewKind, AttachedReviewMetaDocument> documents = [];
    private readonly List<ScopeExclusion> exclusions = [];

    public HierarchyNode(string id, string name, ReviewLevel level, string path, long? sizeBytes = null, int? lineCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Id = id;
        Name = name;
        Level = level;
        Path = path;
        SizeBytes = sizeBytes;
        LineCount = lineCount;
    }

    public string Id { get; }

    public string Name { get; }

    public ReviewLevel Level { get; }

    public string Path { get; }

    /// <summary>Physical source information, present only for file nodes.</summary>
    public long? SizeBytes { get; }

    public int? LineCount { get; }

    public IReadOnlyList<HierarchyNode> Children => children;

    /// <summary>Files and project inputs excluded from this aggregate subtree.</summary>
    public IReadOnlyList<ScopeExclusion> Exclusions => exclusions;

    /// <summary>At most one independently authored document per review kind.</summary>
    public IReadOnlyDictionary<ReviewKind, AttachedReviewMetaDocument> Documents =>
        new ReadOnlyDictionary<ReviewKind, AttachedReviewMetaDocument>(documents);

    /// <summary>Per-kind direct and rolled-up state for this node's current subtree.</summary>
    public IReadOnlyDictionary<ReviewKind, KindAggregation> AggregatedStates =>
        HierarchyAggregation.ForAllKinds(this);

    public HierarchyNode AddChild(HierarchyNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if ((int)child.Level != (int)Level + 1)
        {
            throw new ArgumentException($"A {Level} node can only contain the next hierarchy level.", nameof(child));
        }

        children.Add(child);
        return this;
    }

    public HierarchyNode AddExclusion(ScopeExclusion exclusion)
    {
        ArgumentNullException.ThrowIfNull(exclusion);
        if (!exclusions.Contains(exclusion)) exclusions.Add(exclusion);
        return this;
    }

    public HierarchyNode AddExclusions(IEnumerable<ScopeExclusion> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values) AddExclusion(value);
        return this;
    }

    public HierarchyNode Attach(AttachedReviewMetaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!StringComparer.Ordinal.Equals(document.UnitId, Id))
        {
            throw new ArgumentException("The document belongs to a different hierarchy unit.", nameof(document));
        }

        if (!documents.TryAdd(document.Kind, document))
        {
            throw new InvalidOperationException($"Unit '{Id}' already has a {document.Kind} review document.");
        }

        return this;
    }
}
