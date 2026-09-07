using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityWorkspaceTests
{
    [Fact]
    public void Data_root_is_outside_the_analysed_checkout()
    {
        using var checkout = TemporaryDirectory.Create("quality-workspace-outside-");

        var workspace = QualityWorkspace.ForRepository(checkout.Path);

        Assert.False(workspace.DataRoot.StartsWith(workspace.RepositoryRoot, StringComparison.Ordinal),
            $"the data root '{workspace.DataRoot}' must not sit inside the checkout '{workspace.RepositoryRoot}'.");
    }

    [Fact]
    public void The_same_checkout_always_resolves_to_the_same_project()
    {
        using var checkout = TemporaryDirectory.Create("quality-workspace-stable-");

        var first = QualityWorkspace.ForRepository(checkout.Path);
        // A trailing separator and a redundant "." segment name the same directory, so they must not
        // produce a second data root that silently hides the first one's data.
        var second = QualityWorkspace.ForRepository(
            checkout.Path + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar);

        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.Equal(first.DataRoot, second.DataRoot);
    }

    [Fact]
    public void Two_checkouts_of_one_repository_do_not_share_a_data_root()
    {
        using var first = TemporaryDirectory.Create("quality-workspace-worktree-");
        using var second = TemporaryDirectory.Create("quality-workspace-worktree-");

        // Two worktrees of one repository hold different content at different commits. Keying the
        // data root on the path rather than on the Git remote keeps their derived data apart.
        Assert.NotEqual(
            QualityWorkspace.ForRepository(first.Path).DataRoot,
            QualityWorkspace.ForRepository(second.Path).DataRoot);
    }

    [Fact]
    public void Project_id_carries_a_readable_slug_of_the_checkout()
    {
        using var parent = TemporaryDirectory.Create("quality-workspace-slug-");
        var checkout = parent.CreateSubdirectory("Quality.Studio");

        var workspace = QualityWorkspace.ForRepository(checkout);

        Assert.StartsWith("quality-studio-", workspace.ProjectId, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_data_root_overrides_the_environment()
    {
        using var checkout = TemporaryDirectory.Create("quality-workspace-explicit-");
        using var data = TemporaryDirectory.Create("quality-workspace-explicit-data-");

        var workspace = QualityWorkspace.At(checkout.Path, data.Path);

        Assert.Equal(Path.GetFullPath(data.Path), workspace.DataRoot);
        Assert.Equal(Path.Combine(workspace.DataRoot, "reports", "runs"), workspace.Combine("reports/runs"));
    }

    [Fact]
    public void Authored_inputs_are_still_addressed_inside_the_checkout()
    {
        using var checkout = TemporaryDirectory.Create("quality-workspace-inputs-");

        var workspace = QualityWorkspace.ForRepository(checkout.Path);

        Assert.Equal(
            Path.Combine(workspace.RepositoryRoot, ".quality", "scope.json"),
            workspace.InRepository(".quality/scope.json"));
    }

    [Fact]
    public void The_data_root_records_which_checkout_it_belongs_to()
    {
        using var checkout = TemporaryDirectory.Create("quality-workspace-descriptor-");

        var workspace = QualityWorkspace.ForRepository(checkout.Path);
        workspace.EnsureCreated();

        var descriptor = File.ReadAllText(Path.Combine(workspace.DataRoot, QualityWorkspace.DescriptorFileName));
        Assert.Contains(workspace.ProjectId, descriptor, StringComparison.Ordinal);
        Assert.Contains("repositoryRoot", descriptor, StringComparison.Ordinal);
    }
}
