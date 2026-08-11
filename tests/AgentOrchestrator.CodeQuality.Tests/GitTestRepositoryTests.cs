namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", TestCategories.ToolBound)]
public sealed class GitTestRepositoryTests
{
    [Fact]
    public void Create_configures_a_deterministic_cross_platform_identity()
    {
        using var repository = GitTestRepository.Create("quality-studio-git-fixture-");

        Assert.Equal("Quality Studio Fixture", repository.Run("config", "user.name").Trim());
        Assert.Equal("fixture@quality-studio.test", repository.Run("config", "user.email").Trim());
        Assert.Equal("false", repository.Run("config", "core.autocrlf").Trim());
    }
}
