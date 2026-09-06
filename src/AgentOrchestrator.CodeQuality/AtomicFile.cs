using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Replaces a file in a single observable step. The content is written to a temporary sibling in
/// the destination directory, flushed to the storage device, and only then moved over the
/// destination, so a reader sees either the previous file or the complete new one — never a
/// truncated or half-flushed file after a crash or power loss.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        var destination = PrepareDestination(path);
        var temporary = TemporaryPath(destination);
        try
        {
            var bytes = Utf8NoBom.GetBytes(content);
            await using (var stream = Create(temporary, asynchronous: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            Discard(temporary);
        }
    }

    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        var destination = PrepareDestination(path);
        var temporary = TemporaryPath(destination);
        try
        {
            var bytes = Utf8NoBom.GetBytes(content);
            using (var stream = Create(temporary, asynchronous: false))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            Discard(temporary);
        }
    }

    private static string PrepareDestination(string path)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException($"An atomic write needs a file inside a directory: {path}", nameof(path));
        Directory.CreateDirectory(directory);
        return destination;
    }

    // The temporary lives next to its destination so the move stays within one volume, and it never
    // ends in the destination's extension so sidecar and report scans cannot pick it up mid-write.
    private static string TemporaryPath(string destination) =>
        destination + ".tmp-" + Guid.NewGuid().ToString("N");

    private static FileStream Create(string path, bool asynchronous)
    {
        var options = FileOptions.WriteThrough | (asynchronous ? FileOptions.Asynchronous : FileOptions.None);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, options);
    }

    private static void Discard(string temporary)
    {
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary must never replace the failure that produced it.
        }
    }
}
