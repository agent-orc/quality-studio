using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>The size-weighted directory grade projection, independent of any hierarchy.</summary>
public sealed class GradeProjectionTests
{
    [Fact]
    public void Files_weigh_by_line_count_and_ungraded_files_count_only_as_files()
    {
        var rollup = GradeRollup.ForFile(90, 300, null) +
                     GradeRollup.ForFile(50, 100, null) +
                     GradeRollup.ForFile(null, 5_000, null);

        var projection = rollup.ToProjection()!;

        // (90 * 300 + 50 * 100) / 400 = 80: the larger file dominates, the ungraded one is not a zero.
        Assert.Equal(80, projection.Score);
        Assert.Equal("B", projection.Band);
        Assert.Equal(2, projection.GradedFiles);
        Assert.Equal(3, projection.Files);
        Assert.Equal(400, projection.WeightedLines);
        Assert.Equal(GradeProjectionResponse.SizeWeightedFileGrades, projection.Basis);
    }

    [Fact]
    public void Without_any_graded_file_there_is_no_projection()
    {
        Assert.Null((GradeRollup.ForFile(null, 10, null) + default(GradeRollup)).ToProjection());
        Assert.Null(default(GradeRollup).ToProjection());
    }

    [Fact]
    public void A_file_without_a_line_count_weighs_by_size_and_never_by_zero()
    {
        Assert.Equal(25, GradeRollup.ForFile(70, null, 1_000).Weight);
        Assert.Equal(1, GradeRollup.ForFile(70, 0, 0).Weight);
    }

    [Fact]
    public void Explorer_directories_without_a_review_project_their_files_grades()
    {
        var root = Directory.CreateTempSubdirectory("qs-projection-").FullName;
        try
        {
            var api = new HierarchyNode("ns-api", "Api", ReviewLevel.Namespace, "backend/api");
            api.AddChild(Graded(new HierarchyNode("big", "Big.cs", ReviewLevel.File, "backend/api/Big.cs", 9_000, 300), 90));
            api.AddChild(Graded(new HierarchyNode("small", "Small.cs", ReviewLevel.File, "backend/api/Small.cs", 3_000, 100), 50));
            var web = new HierarchyNode("ns-web", "Web", ReviewLevel.Namespace, "backend/web");
            web.AddChild(new HierarchyNode("none", "None.cs", ReviewLevel.File, "backend/web/None.cs", 150_000, 5_000));
            HierarchyNode[] roots = [api, web];

            var index = TreeProjectionIndex.Create(roots, new Dictionary<string, FindingStateRecord>(), null, null);
            var explorer = index.GetExplorer(root, roots);

            var repository = explorer.FindPath(".")!.Kinds["code"];
            Assert.Null(repository.Score);
            Assert.Equal(new GradeProjectionResponse(80, "B", 2, 3, 400), repository.Projection);
            Assert.Equal(80, explorer.FindPath("backend/api")!.Kinds["code"].Projection!.Score);
            Assert.Null(explorer.FindPath("backend/web")!.Kinds["code"].Projection);
            Assert.Null(explorer.FindPath("backend/api/Big.cs")!.Kinds["code"].Projection);
            // No file carries a security review, so there is nothing to project for that kind.
            Assert.Null(explorer.FindPath(".")!.Kinds["security"].Projection);
            // The canonical tree projects the same way for a namespace without its own review.
            Assert.Equal(80, index.Get(api).Kinds["code"].Projection!.Score);
            Assert.Null(index.Get(web).Kinds["code"].Projection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HierarchyNode Graded(HierarchyNode file, int score) => file.Attach(new AttachedReviewMetaDocument(
        file.Id, ReviewKind.Code, ReviewState.Current, file.Path + ".code.meta.json",
        $$"""{"grade":{"score":{{score}},"band":"{{GradeRollup.BandOf(score)}}","rationale":"Fixture."},"findings":[]}"""));

    [Theory]
    [InlineData(90, "A")]
    [InlineData(89, "B")]
    [InlineData(70, "C")]
    [InlineData(60, "D")]
    [InlineData(59, "F")]
    public void Bands_follow_the_review_grade_scale(int score, string band) =>
        Assert.Equal(band, GradeRollup.BandOf(score));
}
