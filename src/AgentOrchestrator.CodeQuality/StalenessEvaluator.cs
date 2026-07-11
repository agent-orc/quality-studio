using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed class StalenessEvaluator
{
    public async Task<StalenessReport> ScanAsync(
        string repositoryRoot,
        StalenessEvaluatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<FileStaleness>();
        await foreach (var file in EvaluateAsync(repositoryRoot, options, cancellationToken))
        {
            files.Add(file);
        }

        return new StalenessReport(files);
    }

    public async IAsyncEnumerable<FileStaleness> EvaluateAsync(
        string repositoryRoot,
        StalenessEvaluatorOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new StalenessEvaluatorOptions();
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        ValidateOptions(options);
        var includePatterns = options.IncludeGlobs.Select(GlobToRegex).ToArray();
        var stopwatch = Stopwatch.StartNew();
        QualityStudioEventSource.Log.ScanStarted(root, options.ReviewKind);
        var count = 0;
        try
        {
            var repositoryFiles = EnumerateGitFilesAsync(root, cancellationToken);
            var metaBySubject = await LoadMetadataAsync(repositoryFiles, root, options.ReviewKind, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var relativePath in EnumerateGitFilesAsync(root, cancellationToken))
            {
                if (IsInfrastructurePath(relativePath) || !includePatterns.Any(pattern => pattern.IsMatch(relativePath)))
                {
                    continue;
                }

                count++;
                if (!metaBySubject.TryGetValue(relativePath, out var metadata))
                {
                    yield return new FileStaleness(relativePath, StalenessState.Missing, options.ReviewKind);
                    continue;
                }

                var state = await EvaluateMetadataAsync(root, metadata, cancellationToken).ConfigureAwait(false);
                yield return new FileStaleness(relativePath, state, options.ReviewKind, metadata.MetaRelativePath);
            }
        }
        finally
        {
            QualityStudioEventSource.Log.ScanCompleted(root, count, stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<StalenessState> EvaluateMetadataAsync(
        string root,
        ReviewMetadata metadata,
        CancellationToken cancellationToken)
    {
        var currentInputs = new List<SubjectInputHash>(metadata.Inputs.Count);
        try
        {
            foreach (var input in metadata.Inputs)
            {
                if (!string.Equals(input.Selector, "file", StringComparison.Ordinal))
                {
                    return StalenessState.Stale;
                }

                var absolutePath = ResolveWithinRoot(root, input.Path);
                var contentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(absolutePath, cancellationToken)
                    .ConfigureAwait(false);
                currentInputs.Add(new SubjectInputHash(input.Path, input.Selector, contentHash));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return StalenessState.Stale;
        }

        var currentHash = ReviewSubjectHasher.ComputeManifestHash(metadata.UnitId, currentInputs);
        return string.Equals(currentHash, metadata.ReviewedHash, StringComparison.Ordinal)
            ? StalenessState.Fresh
            : StalenessState.Stale;
    }

    private static async Task<Dictionary<string, ReviewMetadata>> LoadMetadataAsync(
        IAsyncEnumerable<string> repositoryFiles,
        string root,
        string reviewKind,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ReviewMetadata>(StringComparer.Ordinal);
        await foreach (var relativePath in repositoryFiles)
        {
            if (!IsMetaPath(relativePath))
            {
                continue;
            }

            ReviewMetadata? metadata;
            try
            {
                await using var stream = File.OpenRead(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var json = document.RootElement;
                var kind = json.GetProperty("kind").GetString();
                if (!string.Equals(kind, reviewKind, StringComparison.Ordinal))
                {
                    continue;
                }

                var unit = json.GetProperty("unit");
                if (!string.Equals(unit.GetProperty("level").GetString(), "file", StringComparison.Ordinal))
                {
                    continue;
                }

                var inputs = json.GetProperty("subjectInputs").EnumerateArray()
                    .Select(input => new StoredSubjectInput(
                        NormalizeRelativePath(input.GetProperty("path").GetString()!),
                        input.GetProperty("selector").GetString()!))
                    .ToArray();
                metadata = new ReviewMetadata(
                    NormalizeRelativePath(unit.GetProperty("path").GetString()!),
                    unit.GetProperty("id").GetString()!,
                    json.GetProperty("reviewedHash").GetProperty("value").GetString()!,
                    NormalizeRelativePath(relativePath),
                    inputs);
            }
            catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new StalenessScanException($"Cannot read review metadata '{relativePath}'.", exception);
            }

            if (!result.TryAdd(metadata.SubjectPath, metadata))
            {
                throw new StalenessScanException(
                    $"Multiple '{reviewKind}' review metadata files target '{metadata.SubjectPath}'.");
            }
        }

        return result;
    }

    private static async IAsyncEnumerable<string> EnumerateGitFilesAsync(
        string root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("ls-files");
        process.StartInfo.ArgumentList.Add("--cached");
        process.StartInfo.ArgumentList.Add("--others");
        process.StartInfo.ArgumentList.Add("--exclude-standard");
        process.StartInfo.ArgumentList.Add("-z");

        try
        {
            if (!process.Start())
            {
                throw new StalenessScanException("Git file enumeration did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new StalenessScanException("Git is required to enumerate files with .gitignore semantics.", exception);
        }

        var buffer = new char[4096];
        var pathBuilder = new StringBuilder();
        int charactersRead;
        while ((charactersRead = await process.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var index = 0; index < charactersRead; index++)
            {
                if (buffer[index] == '\0')
                {
                    yield return NormalizeRelativePath(pathBuilder.ToString());
                    pathBuilder.Clear();
                }
                else
                {
                    pathBuilder.Append(buffer[index]);
                }
            }
        }

        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new StalenessScanException($"Git file enumeration failed: {error.Trim()}");
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        var normalized = NormalizeRelativePath(glob);
        var pattern = Regex.Escape(normalized)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex("^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new StalenessScanException($"Review subject escapes the repository: {relativePath}");
        }

        return absolutePath;
    }

    private static bool IsInfrastructurePath(string path) =>
        path.Split('/').Any(segment => segment is ".quality" or ".git" or "bin" or "obj") ||
        IsMetaPath(path);

    private static bool IsMetaPath(string path) =>
        (path.StartsWith(".quality/reviews/", StringComparison.Ordinal) ||
         path.Contains("/.quality/reviews/", StringComparison.Ordinal)) &&
        path.Contains(".review-meta.", StringComparison.Ordinal) &&
        path.EndsWith(".json", StringComparison.Ordinal);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void ValidateOptions(StalenessEvaluatorOptions options)
    {
        if (options.IncludeGlobs.Count == 0 || options.IncludeGlobs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty include glob is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ReviewKind))
        {
            throw new ArgumentException("A review kind is required.", nameof(options));
        }
    }

    private sealed record StoredSubjectInput(string Path, string Selector);

    private sealed record ReviewMetadata(
        string SubjectPath,
        string UnitId,
        string ReviewedHash,
        string MetaRelativePath,
        IReadOnlyList<StoredSubjectInput> Inputs);
}

[EventSource(Name = "AgentOrchestrator-CodeQuality")]
internal sealed class QualityStudioEventSource : EventSource
{
    public static readonly QualityStudioEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void ScanStarted(string repositoryRoot, string reviewKind) => WriteEvent(1, repositoryRoot, reviewKind);

    [Event(2, Level = EventLevel.Informational)]
    public void ScanCompleted(string repositoryRoot, int fileCount, long elapsedMilliseconds) =>
        WriteEvent(2, repositoryRoot, fileCount, elapsedMilliseconds);

    [Event(3, Level = EventLevel.Informational)]
    public void ReviewStarted(string filePath, string kind, string agent) => WriteEvent(3, filePath, kind, agent);

    [Event(4, Level = EventLevel.Informational)]
    public void ReviewCompleted(string filePath, string kind, string runId, long elapsedMilliseconds) =>
        WriteEvent(4, filePath, kind, runId, elapsedMilliseconds);

    [Event(5, Level = EventLevel.Error)]
    public void ReviewFailed(string filePath, string kind, string errorType, string message) =>
        WriteEvent(5, filePath, kind, errorType, message);
}
