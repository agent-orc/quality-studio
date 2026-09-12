using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class HierarchyAggregationTests
{
    [Fact]
    public void MissingDocumentIsExplicitAtTheCurrentLevel()
    {
        var tree = Tree();
        tree.Children[0].Children[0].Children[0].Attach(Document("file", ReviewKind.Code, ReviewState.Current));

        var result = HierarchyAggregation.For(tree.Children[0], ReviewKind.Code);

        Assert.Equal(ReviewState.NotReviewed, result.Direct);
        Assert.Equal(ReviewState.Current, result.Descendants);
        Assert.Equal(ReviewState.Current, result.Overall);
    }

    [Fact]
    public void StaleIsWorstAmongReviewedDescendants()
    {
        var module = Tree().Children[0];
        var ns = module.Children[0];
        ns.Children[0].Attach(Document("file", ReviewKind.Security, ReviewState.Current));
        ns.Children[1].Attach(Document("file-2", ReviewKind.Security, ReviewState.Stale));

        var result = HierarchyAggregation.For(module, ReviewKind.Security);

        Assert.Equal(ReviewState.Stale, result.Overall);
    }

    [Fact]
    public void KindsAreAggregatedIndependentlyOnTheSameFile()
    {
        var file = Tree().Children[0].Children[0].Children[0];
        file.Attach(Document("file", ReviewKind.Code, ReviewState.Current));
        file.Attach(Document("file", ReviewKind.Performance, ReviewState.Stale));

        var states = HierarchyAggregation.ForAllKinds(file);

        Assert.Equal(ReviewState.Current, states[ReviewKind.Code].Overall);
        Assert.Equal(ReviewState.NotReviewed, states[ReviewKind.Security].Overall);
        Assert.Equal(ReviewState.Stale, states[ReviewKind.Performance].Overall);
        Assert.Equal(2, file.Documents.Count);
    }

    [Fact]
    public void EmptySubtreeIsNotReviewedRatherThanStale()
    {
        Assert.Equal(ReviewState.NotReviewed, HierarchyAggregation.For(Tree(), ReviewKind.Code).Overall);
    }

    [Fact]
    public void RejectsDuplicateKindAtOneLevel()
    {
        var file = Tree().Children[0].Children[0].Children[0];
        file.Attach(Document("file", ReviewKind.Code, ReviewState.Current));

        Assert.Throws<InvalidOperationException>(() =>
            file.Attach(Document("file", ReviewKind.Code, ReviewState.Stale)));
    }

    private static AttachedReviewMetaDocument Document(string id, ReviewKind kind, ReviewState state) =>
        new(id, kind, state, $"{id}.{kind}.json");

    private static HierarchyNode Tree()
    {
        var project = new HierarchyNode("project", "Demo", ReviewLevel.Project, ".");
        var module = new HierarchyNode("module", "Core", ReviewLevel.Module, "Core.csproj");
        var ns = new HierarchyNode("namespace", "Demo", ReviewLevel.Namespace, "Core.csproj");
        var first = new HierarchyNode("file", "One.cs", ReviewLevel.File, "One.cs");
        first.AddChild(new HierarchyNode("function", "Run", ReviewLevel.Function, "One.cs"));
        ns.AddChild(first);
        ns.AddChild(new HierarchyNode("file-2", "Two.cs", ReviewLevel.File, "Two.cs"));
        module.AddChild(ns);
        project.AddChild(module);
        return project;
    }
}
