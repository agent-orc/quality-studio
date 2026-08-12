using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Boundary", "Git")]
public sealed class GitFixtureTests
{
    [Fact]
    public void Initializes_reproducible_identity_timestamps_and_failure_diagnostics()
    {
        var root = GitFixture.Create("quality-studio-git-fixture-tests");
        try
        {
            File.WriteAllText(Path.Combine(root, "fixture.txt"), "fixture\n");
            GitFixture.Run(root, "add", ".");
            GitFixture.Run(root, "commit", "--quiet", "-m", "fixture");

            Assert.Equal("Quality Studio Tests", GitFixture.Run(root, "show", "-s", "--format=%an"));
            Assert.Equal("2026-01-01T00:00:00+00:00", GitFixture.Run(root, "show", "-s", "--format=%aI"));
            var exception = Assert.Throws<InvalidOperationException>(() =>
                GitFixture.Run(root, "rev-parse", "--verify", "missing-ref"));
            Assert.Contains("git rev-parse --verify missing-ref failed", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }
}
