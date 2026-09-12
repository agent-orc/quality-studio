namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewTaxonomyTests
{
    [Fact]
    public void ReviewKindsMatchTheSupportedQualityCharacteristics()
    {
        ReviewKind[] expected = [ReviewKind.Code, ReviewKind.Security, ReviewKind.Performance];

        Assert.Equal(expected, Enum.GetValues<ReviewKind>());
    }

    [Fact]
    public void ReviewLevelsFollowTheRepositoryHierarchy()
    {
        ReviewLevel[] expected =
        [
            ReviewLevel.Project,
            ReviewLevel.Module,
            ReviewLevel.Namespace,
            ReviewLevel.File,
            ReviewLevel.Function,
        ];

        Assert.Equal(expected, Enum.GetValues<ReviewLevel>());
    }
}
