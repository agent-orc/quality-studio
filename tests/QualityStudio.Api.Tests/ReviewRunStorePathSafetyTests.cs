using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// A run id becomes a journal directory name, and retention deletes that directory recursively. The host
/// only ever mints server-side ids, and <see cref="ReviewRunStore.LoadAll"/> refuses a journal whose
/// manifest disagrees with its directory, so no caller reaches these ids today. The guard is the last line
/// behind both: it keeps a journal path a single child of the runs root on either platform.
/// </summary>
public sealed class ReviewRunStorePathSafetyTests
{
    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("..")]
    [InlineData(".")]
    public void A_run_id_that_is_not_a_single_directory_name_is_rejected(string runId)
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-run-id-guard-").FullName;
        try
        {
            var store = new ReviewRunStore(root);

            var rejected = Assert.Throws<ArgumentException>(() =>
                store.WriteObservation(runId, "operation-1", Snapshot()));

            Assert.Equal("runId", rejected.ParamName);
            Assert.Contains("single directory name", rejected.Message, StringComparison.Ordinal);
            // Nothing was created next to, above or inside the runs root.
            Assert.False(Directory.Exists(store.RunsPath));
            Assert.Equal([], Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void A_backslash_id_is_rejected_on_Linux_too_so_a_journal_means_the_same_on_both_platforms()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-run-id-portable-").FullName;
        try
        {
            var store = new ReviewRunStore(root);

            // On Linux a backslash is an ordinary filename character, so the previous separator check
            // passed and the journal was written; read back on Windows the same id escapes the runs root.
            Assert.Throws<ArgumentException>(() =>
                store.WriteObservation("..\\..\\quality", "operation-1", Snapshot()));

            store.WriteObservation("review-abc", "operation-1", Snapshot());
            Assert.True(Directory.Exists(Path.Combine(store.RunsPath, "review-abc")));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    private static ReviewObservationSnapshot Snapshot() => new(
        "Sample.cs.review-meta.code.json",
        new string('0', 64),
        new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
        "{}",
        new Dictionary<string, string>(StringComparer.Ordinal));
}
