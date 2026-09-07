using System.Security.Cryptography;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityDataRootTests
{
    [Fact]
    public void For_resolves_one_checkout_to_one_directory_below_the_configured_base()
    {
        using var checkout = TemporaryDirectory.Create("quality-data-root");

        var dataRoot = QualityDataRoot.For(checkout.Path);

        Assert.Equal(dataRoot, QualityDataRoot.For(checkout.Path));
        Assert.Equal(dataRoot, QualityDataRoot.For(checkout.Path + Path.DirectorySeparatorChar));
        Assert.Equal(
            Path.Combine(
                QualityDataRoot.BaseDirectory,
                QualityDataRoot.ProjectsDirectoryName,
                QualityDataRoot.ProjectKey(checkout.Path)),
            dataRoot);
    }

    /// <summary>
    /// A second clone and a Git worktree of the same remote usually carry the same directory name,
    /// and each is a separately analysed project that must not share the other's ledger.
    /// </summary>
    [Fact]
    public void For_separates_two_checkouts_that_share_a_directory_name()
    {
        using var first = TemporaryDirectory.Create("quality-data-root");
        using var second = TemporaryDirectory.Create("quality-data-root");
        var firstClone = first.CreateSubdirectory("acme");
        var secondClone = second.CreateSubdirectory("acme");

        Assert.NotEqual(QualityDataRoot.For(firstClone), QualityDataRoot.For(secondClone));
        Assert.StartsWith("acme-", Path.GetFileName(QualityDataRoot.For(firstClone)), StringComparison.Ordinal);
        Assert.StartsWith("acme-", Path.GetFileName(QualityDataRoot.For(secondClone)), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectKey_reads_as_a_slug_of_the_directory_name_and_a_digest_of_its_path()
    {
        using var parent = TemporaryDirectory.Create("quality-data-root");
        var checkout = parent.CreateSubdirectory("Acme.Web Client");

        var key = QualityDataRoot.ProjectKey(checkout);

        Assert.Matches("^acme-web-client-[a-f0-9]{12}$", key);
    }

    [Fact]
    public async Task Run_moves_generated_data_to_the_data_root_and_leaves_authored_inputs_in_the_checkout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var checkout = TemporaryDirectory.Create("quality-data-migration");
        var root = checkout.Path;
        var sidecar = ReviewMetaPath.ForFile(root, "src/Foo/Bar.cs", "code");
        var legacySidecar = Path.Combine(
            root, "src", "Foo", ".quality", "reviews", "files", Path.GetFileName(sidecar));
        await WriteAsync(Path.Combine(root, ".quality", "findings", "state.json"), "{\"findings\":[]}", cancellationToken);
        await WriteAsync(Path.Combine(root, ".quality", "usage", "2026-09.jsonl"), "{\"runId\":\"r0\"}\n", cancellationToken);
        await WriteAsync(Path.Combine(root, ".quality", "reports", "runs", "r1.json"), "{\"runId\":\"r1\"}", cancellationToken);
        await WriteAsync(legacySidecar, "{\"schemaVersion\":3}", cancellationToken);
        var input = Path.Combine(root, ".quality", "inputs", "style.md");
        await WriteAsync(input, "---\nid: style\n---\nPrefer explicit names.\n", cancellationToken);

        var report = QualityDataMigration.Run(root);

        Assert.Equal(QualityDataRoot.For(root), report.DataRoot);
        Assert.Equal(4, report.MovedFiles);
        Assert.Equal("{\"findings\":[]}", await File.ReadAllTextAsync(
            QualityDataRoot.Combine(root, "findings", "state.json"), cancellationToken));
        Assert.Equal("{\"runId\":\"r0\"}\n", await File.ReadAllTextAsync(
            UsageLedger.GetLedgerPath(root, new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)), cancellationToken));
        Assert.Equal("{\"runId\":\"r1\"}", await File.ReadAllTextAsync(
            QualityDataRoot.Combine(root, "reports", "runs", "r1.json"), cancellationToken));
        // The one family that was never rooted at the checkout root lands exactly where a fresh
        // review of the same subject would now write it.
        Assert.Equal("{\"schemaVersion\":3}", await File.ReadAllTextAsync(sidecar, cancellationToken));
        Assert.Equal([sidecar], ReviewMetaPath.Enumerate(root, "code"));
        Assert.False(File.Exists(legacySidecar));
        Assert.False(Directory.Exists(Path.Combine(root, ".quality", "findings")));

        Assert.Equal("---\nid: style\n---\nPrefer explicit names.\n",
            await File.ReadAllTextAsync(input, cancellationToken));
        Assert.Empty(report.RemainingInTree);
        Assert.Empty(QualityDataMigration.RemainingInTree(root));

        var second = QualityDataMigration.Run(root);

        Assert.False(second.MovedAnything);
        Assert.True(File.Exists(sidecar));
        Assert.True(File.Exists(input));
    }

    /// <summary>
    /// The shell cleanup is scoped to the sidecar lanes the run drained. A sweep over every
    /// <c>.quality</c> in the tree would also take folders the author left empty on purpose and the
    /// <c>preflight</c> directory that stays in the checkout by design - silently, since neither is
    /// something the report mentions.
    /// </summary>
    [Fact]
    public async Task Run_leaves_empty_directories_it_did_not_drain_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var checkout = TemporaryDirectory.Create("quality-data-migration-shells");
        var root = checkout.Path;
        var preflight = Path.Combine(root, ".quality", "preflight");
        var reservedInputs = Path.Combine(root, ".quality", "inputs");
        Directory.CreateDirectory(preflight);
        Directory.CreateDirectory(reservedInputs);
        var legacySidecar = Path.Combine(
            root, "src", ".quality", "reviews", "files",
            Path.GetFileName(ReviewMetaPath.ForFile(root, "src/Bar.cs", "code")));
        await WriteAsync(legacySidecar, "{\"schemaVersion\":3}", cancellationToken);

        QualityDataMigration.Run(root);

        Assert.True(Directory.Exists(preflight));
        Assert.True(Directory.Exists(reservedInputs));
        // The lane it did drain is gone, along with the .quality that existed only to hold it.
        Assert.False(Directory.Exists(Path.Combine(root, "src", ".quality")));
    }

    /// <summary>
    /// A migration reports what it could not move instead of throwing halfway. A partly moved
    /// checkout with no record of what already went is the worst state to hand an operator.
    /// </summary>
    [Fact]
    public async Task Run_reports_an_artefact_it_cannot_move_and_still_moves_the_others()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var checkout = TemporaryDirectory.Create("quality-data-migration-blocked");
        var root = checkout.Path;
        await WriteAsync(Path.Combine(root, ".quality", "findings", "state.json"), "{}", cancellationToken);
        await WriteAsync(Path.Combine(root, ".quality", "usage", "2026-09.jsonl"), "{}\n", cancellationToken);
        // The destination of one artefact is occupied by a file, so creating its directory fails.
        await WriteAsync(QualityDataRoot.Combine(root, "findings"), "not a directory", cancellationToken);

        var report = QualityDataMigration.Run(root);

        var findings = Assert.Single(report.Artefacts, item => item.RelativePath == "findings");
        Assert.Equal(MigrationOutcome.Failed, findings.Outcome);
        Assert.NotNull(findings.Error);
        Assert.Single(report.Failures);
        // The artefact after the failure still moved, and the report still describes the whole run.
        Assert.Equal("{}\n", await File.ReadAllTextAsync(
            QualityDataRoot.Combine(root, "usage", "2026-09.jsonl"), cancellationToken));
        Assert.Contains(".quality/findings", report.RemainingInTree);
    }

    [Fact]
    public async Task Run_as_a_dry_run_reports_what_it_would_move_without_moving_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var checkout = TemporaryDirectory.Create("quality-data-migration-dry");
        var root = checkout.Path;
        var legacyState = Path.Combine(root, ".quality", "findings", "state.json");
        var legacySidecar = Path.Combine(
            root, "src", "Foo", ".quality", "reviews", "files",
            Path.GetFileName(ReviewMetaPath.ForFile(root, "src/Foo/Bar.cs", "code")));
        await WriteAsync(legacyState, "{\"findings\":[]}", cancellationToken);
        await WriteAsync(Path.Combine(root, ".quality", "usage", "2026-09.jsonl"), "{\"runId\":\"r0\"}\n", cancellationToken);
        await WriteAsync(Path.Combine(root, ".quality", "reports", "runs", "r1.json"), "{\"runId\":\"r1\"}", cancellationToken);
        await WriteAsync(legacySidecar, "{\"schemaVersion\":3}", cancellationToken);

        var report = QualityDataMigration.Run(root, dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(4, report.MovedFiles);
        Assert.True(File.Exists(legacyState));
        Assert.True(File.Exists(legacySidecar));
        Assert.False(Directory.Exists(QualityDataRoot.For(root)));
        Assert.Equal(
            [".quality/findings", ".quality/reports", ".quality/usage", "src/Foo/.quality/reviews"],
            report.RemainingInTree);
    }

    /// <summary>
    /// The whole point of the data root: Agent Studio refuses to fast-forward a checkout with
    /// uncommitted changes, so a review may not add, remove or rewrite a single file in it.
    /// </summary>
    [Fact]
    public async Task ReviewAsync_leaves_the_analysed_checkout_byte_for_byte_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var checkout = TemporaryDirectory.Create("quality-data-root-review");
        var root = checkout.Path;
        checkout.CreateSubdirectory("src");
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Small.cs"), "internal static class Small { }\n", cancellationToken);
        var before = Snapshot(root);

        var result = await new ReviewRunner(new StubAgent()).ReviewAsync(
            new ReviewRequest("src/Small.cs", RepositoryRoot: root), cancellationToken);

        Assert.Equal(before, Snapshot(root));
        // Without this the assertion above would also hold for a review that did nothing at all.
        Assert.Equal(ReviewMetaPath.ForFile(root, "src/Small.cs", "code"), result.MetaPath);
        Assert.True(File.Exists(result.MetaPath));
        Assert.StartsWith(
            QualityDataRoot.For(root) + Path.DirectorySeparatorChar, result.MetaPath, StringComparison.Ordinal);
    }

    /// <summary>Every file under the checkout by content, with Git's own bookkeeping left out.</summary>
    private static SortedDictionary<string, string> Snapshot(string root) => new(
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(relative => !relative.Split('/').Contains(".git"))
            .ToDictionary(
                relative => relative,
                relative => Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative)))),
                StringComparer.Ordinal),
        StringComparer.Ordinal);

    private static Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private sealed class StubAgent : IReviewAgent
    {
        public string AgentName => "test-agent";

        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(
            string prompt, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                "run-test",
                $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(120, 34, 56, 7, 890),
                "deterministic"));
    }
}

/// <summary>
/// The base directory is process-wide state the test assembly's module initializer owns. A test
/// that moves it has to run alone and put it back, or every store constructed elsewhere in the run
/// files its data somewhere no other test looks - including the developer's own data root.
/// </summary>
[CollectionDefinition("QualityDataRootBaseDirectory", DisableParallelization = true)]
public sealed class QualityDataRootBaseDirectoryCollection
{
}

[Collection("QualityDataRootBaseDirectory")]
public sealed class QualityDataRootBaseDirectoryTests
{
    [Fact]
    public void Configure_overrides_the_base_directory_and_null_restores_the_default_resolution()
    {
        using var checkout = TemporaryDirectory.Create("quality-data-root");
        using var overridden = TemporaryDirectory.Create("quality-data-root-base");
        try
        {
            QualityDataRoot.Configure(overridden.Path);

            Assert.Equal(overridden.Path, QualityDataRoot.BaseDirectory);
            Assert.StartsWith(
                Path.Combine(overridden.Path, QualityDataRoot.ProjectsDirectoryName) + Path.DirectorySeparatorChar,
                QualityDataRoot.For(checkout.Path),
                StringComparison.Ordinal);

            QualityDataRoot.Configure(null);

            Assert.Equal(QualityDataRootFixture.BaseDirectory, QualityDataRoot.BaseDirectory);
        }
        finally
        {
            QualityDataRoot.Configure(QualityDataRootFixture.BaseDirectory);
        }
    }

    [Fact]
    public void Default_resolution_honours_the_data_root_environment_variable()
    {
        using var checkout = TemporaryDirectory.Create("quality-data-root");
        using var fromEnvironment = TemporaryDirectory.Create("quality-data-root-environment");
        var previous = Environment.GetEnvironmentVariable(QualityDataRoot.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(QualityDataRoot.EnvironmentVariable, fromEnvironment.Path);
            QualityDataRoot.Configure(null);

            Assert.Equal(fromEnvironment.Path, QualityDataRoot.BaseDirectory);
            Assert.Equal(
                Path.Combine(
                    fromEnvironment.Path,
                    QualityDataRoot.ProjectsDirectoryName,
                    QualityDataRoot.ProjectKey(checkout.Path)),
                QualityDataRoot.For(checkout.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable(QualityDataRoot.EnvironmentVariable, previous);
            QualityDataRoot.Configure(QualityDataRootFixture.BaseDirectory);
        }
    }
}
