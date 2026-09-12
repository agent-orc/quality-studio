using System.Text;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task Write_creates_missing_directories_and_stores_utf8_without_a_bom()
    {
        var root = Directory.CreateTempSubdirectory("quality-atomic-file-").FullName;
        try
        {
            var path = Path.Combine(root, "nested", "deeper", "document.json");

            await AtomicFile.WriteAllTextAsync(path, "{ \"café\": true }", TestContext.Current.CancellationToken);

            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.NotEqual(Encoding.UTF8.Preamble.ToArray(), bytes.Take(3).ToArray());
            Assert.Equal("{ \"café\": true }", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Write_replaces_an_existing_file_and_leaves_no_temporary_behind()
    {
        var root = Directory.CreateTempSubdirectory("quality-atomic-file-").FullName;
        try
        {
            var path = Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(path, "old and considerably longer", TestContext.Current.CancellationToken);

            await AtomicFile.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);
            AtomicFile.WriteAllText(path, "newest");

            Assert.Equal("newest", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal([path], Directory.GetFiles(root));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task A_failed_write_keeps_the_previous_file_and_discards_the_temporary()
    {
        var root = Directory.CreateTempSubdirectory("quality-atomic-file-").FullName;
        try
        {
            // A directory at the destination cannot be replaced by a file, so the move fails
            // after the temporary was written; the temporary must not survive that failure.
            var path = Path.Combine(root, "occupied");
            Directory.CreateDirectory(path);

            // Windows reports the blocked move as UnauthorizedAccessException, Linux as IOException.
            await Assert.ThrowsAnyAsync<SystemException>(() =>
                AtomicFile.WriteAllTextAsync(path, "replacement", TestContext.Current.CancellationToken));

            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Write_waits_out_a_handle_that_briefly_blocks_the_replace()
    {
        // On Windows a scanner or the search indexer opens a freshly written file for a
        // moment and the replace is refused while that handle lives. The condition clears in
        // milliseconds, so a correct write must not fail because of what else runs on the host.
        var root = Directory.CreateTempSubdirectory("quality-atomic-file-").FullName;
        try
        {
            var path = Path.Combine(root, "document.json");
            await AtomicFile.WriteAllTextAsync(path, "first", TestContext.Current.CancellationToken);

            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = Task.Run(async () =>
            {
                // FileShare.Read denies deletion, which is what a replace of this file needs.
                using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                opened.SetResult();
                await Task.Delay(150, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            await opened.Task;

            await AtomicFile.WriteAllTextAsync(path, "second", TestContext.Current.CancellationToken);
            await released;

            Assert.Equal("second", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }
}
