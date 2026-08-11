using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Versioned repository-input and review-output limits enforced before allocation or agent launch.</summary>
public sealed record ReviewContentLimits
{
    public const int CurrentSchemaVersion = 1;
    public static ReviewContentLimits Default { get; } = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long MaxFileBytes { get; init; } = 1024 * 1024;
    public int MaxAggregateFiles { get; init; } = 200;
    public long MaxAggregateBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxPromptBytes { get; init; } = 6 * 1024 * 1024;
    public long MaxPromptTokens { get; init; } = 1_500_000;
    public long MaxSidecarBytes { get; init; } = 2 * 1024 * 1024;
    public long MaxSidecarAggregateBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxSidecarCount { get; init; } = 2_000;
    public long MaxResponseBytes { get; init; } = 1024 * 1024;
    public int MaxFindings { get; init; } = 200;
    public int MaxThreads { get; init; } = 200;
    public int MaxTextFieldCharacters { get; init; } = 32_000;

    public ReviewContentLimits Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            MaxFileBytes is < 1024 or > 100 * 1024 * 1024 ||
            MaxAggregateFiles is < 1 or > 10_000 ||
            MaxAggregateBytes < MaxFileBytes || MaxAggregateBytes > 500 * 1024 * 1024 ||
            MaxPromptBytes is < 1024 or > 500 * 1024 * 1024 ||
            MaxPromptTokens is < 128 or > 100_000_000 ||
            MaxSidecarBytes is < 1024 or > 100 * 1024 * 1024 ||
            MaxSidecarAggregateBytes < MaxSidecarBytes || MaxSidecarAggregateBytes > 500 * 1024 * 1024 ||
            MaxSidecarCount is < 1 or > 100_000 ||
            MaxResponseBytes is < 1024 or > 100 * 1024 * 1024 ||
            MaxFindings is < 1 or > 10_000 ||
            MaxThreads is < 1 or > 10_000 ||
            MaxTextFieldCharacters is < 128 or > 1_000_000)
            throw new InvalidOperationException("QualityStudio:ContentLimits contains an unsupported version or unsafe bound.");
        return this;
    }
}

public sealed class ReviewContentLimitException(string message) : Exception(message);

/// <summary>Opens only confined regular files and caps bytes before materializing content.</summary>
public static class BoundedRepositoryFile
{
    public static async Task<byte[]> ReadAllBytesAsync(
        string root,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var info = Validate(root, path, maximumBytes);
        if (info.Length > int.MaxValue)
            throw new ReviewContentLimitException("Repository file exceeds the supported allocation size.");
        await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new ReviewContentLimitException($"Repository file exceeds the {maximumBytes}-byte limit.");
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new ReviewContentLimitException($"Repository file exceeds the {maximumBytes}-byte limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static async Task<string> ReadAllTextAsync(
        string root,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllBytesAsync(root, path, maximumBytes, cancellationToken).ConfigureAwait(false);
        return Decode(bytes);
    }

    public static string ReadAllText(string root, string path, long maximumBytes)
    {
        var info = Validate(root, path, maximumBytes);
        return ReadAllText(info, maximumBytes);
    }

    /// <summary>
    /// Reads a file returned by a no-reparse recursive enumeration. Parent traversal was already
    /// checked by the enumerator; the opener still verifies confinement, type, link status, and size.
    /// </summary>
    public static string ReadEnumeratedText(string root, string path, long maximumBytes)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Repository file must be inside its repository root.", nameof(path));
        var info = new FileInfo(normalizedPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.Device) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new FileNotFoundException("Repository regular file was not found.", normalizedPath);
        if (info.Length > maximumBytes)
            throw new ReviewContentLimitException($"Repository file exceeds the {maximumBytes}-byte limit.");
        return ReadAllText(info, maximumBytes);
    }

    private static string ReadAllText(FileInfo info, long maximumBytes)
    {
        using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new ReviewContentLimitException($"Repository file exceeds the {maximumBytes}-byte limit.");
        using var bounded = new BoundedReadStream(stream, maximumBytes);
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);
        return reader.ReadToEnd();
    }

    public static long Length(string root, string path, long maximumBytes) =>
        Validate(root, path, maximumBytes).Length;

    private static FileInfo Validate(string root, string path, long maximumBytes)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Repository file must be inside its repository root.", nameof(path));
        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Repository files cannot traverse symbolic links or junctions.", nameof(path));
        }
        var info = new FileInfo(normalizedPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.Device))
            throw new FileNotFoundException("Repository regular file was not found.", normalizedPath);
        if (info.Length > maximumBytes)
            throw new ReviewContentLimitException($"Repository file exceeds the {maximumBytes}-byte limit.");
        return info;
    }

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long consumed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            consumed += read;
            if (consumed > maximumBytes)
                throw new ReviewContentLimitException($"Repository file exceeds the {maximumBytes}-byte limit.");
            return read;
        }
    }
}
