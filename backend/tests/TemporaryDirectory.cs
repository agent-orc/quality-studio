namespace QualityStudio.Testing;

/// <summary>
/// A temporary directory for test fixtures that survives the two Windows deletion hazards
/// a plain <see cref="Directory.Delete(string, bool)"/> walks into:
/// <list type="bullet">
/// <item>Git writes loose objects under <c>.git/objects</c> with the read-only attribute set,
/// which makes a recursive delete throw <see cref="UnauthorizedAccessException"/>.</item>
/// <item>A directory whose files or handles are still held for a moment - a watcher that has
/// not yet observed the deletion, a background scan, an exiting child process - makes a
/// recursive delete throw <see cref="IOException"/> until the handle is released.</item>
/// </list>
/// Cleanup therefore clears the read-only attribute first and then retries with short
/// backoffs. If the directory still cannot be removed it warns instead of failing: an
/// abandoned temporary fixture must never turn a successful assertion red.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private const int Attempts = 8;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(15);

    private TemporaryDirectory(string path) => Path = path;

    /// <summary>The absolute path of the created directory.</summary>
    public string Path { get; }

    /// <summary>Creates a uniquely named directory below the temp path.</summary>
    public static TemporaryDirectory Create(string prefix = "quality-studio")
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    /// <summary>Combines <see cref="Path"/> with the given segments.</summary>
    public string Combine(params string[] segments) =>
        System.IO.Path.Combine([Path, .. segments]);

    /// <summary>Creates a subdirectory and returns its absolute path.</summary>
    public string CreateSubdirectory(params string[] segments)
    {
        var path = Combine(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => Delete(Path);

    /// <summary>Removes a directory tree, tolerating read-only files and briefly held handles.</summary>
    public static void Delete(string path)
    {
        if (!Directory.Exists(path)) return;

        // The overwhelmingly common case costs exactly one syscall walk; only a fixture that
        // actually hit one of the two hazards pays for attribute clearing and backoffs.
        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var backoff = FirstBackoff;
        Exception? last = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                if (attempt == Attempts) break;
                Thread.Sleep(backoff);
                backoff += backoff;
            }
        }

        Console.Error.WriteLine(
            $"warning: temporary fixture '{path}' could not be removed after {Attempts} attempts " +
            $"({last?.GetType().Name}: {last?.Message}). Leaving it behind rather than failing the test.");
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Enumeration races a concurrent writer or a vanished entry; the delete
            // attempt that follows reports the real reason and is retried.
        }
    }
}
