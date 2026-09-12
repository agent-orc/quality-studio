using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed class StalenessEvaluator
{
    private readonly InputResolver inputResolver;

    public StalenessEvaluator(InputResolver? inputResolver = null) => this.inputResolver = inputResolver ?? new InputResolver();

    public Task<ReviewFreshness> EvaluateReviewAsync(
        string metaPath,
        string currentSubjectHash,
        string currentReviewInputsHash,
        string? requestedModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSubjectHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentReviewInputsHash);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReviewMetaReader.TryLoad(metaPath, out var sidecar, out var error))
        {
            // A sidecar nobody can read is never fresh. The reader has already reported it, and the
            // review that runs instead of the skip replaces the unreadable file.
            _ = error;
            return Task.FromResult(new ReviewFreshness(false, false, false));
        }

        var document = sidecar.Document;
        var modelUnchanged = string.IsNullOrWhiteSpace(requestedModel)
            ? !string.IsNullOrWhiteSpace(document.Reviewer.Model)
            : string.Equals(document.Reviewer.Model, requestedModel.Trim(), StringComparison.Ordinal);
        return Task.FromResult(new ReviewFreshness(
            string.Equals(document.ReviewedHash.Value, currentSubjectHash, StringComparison.Ordinal),
            string.Equals(document.ReviewInputs.EffectiveHash.Value, currentReviewInputsHash, StringComparison.Ordinal),
            modelUnchanged));
    }

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
            // Off the caller's thread: a scan is served on a request thread, and the lane walk plus
            // a parse per sidecar is the one blocking stretch in an otherwise streaming enumeration.
            var index = await Task.Run(
                () => LoadMetadata(root, options.ReviewKind, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            await foreach (var relativePath in EnumerateGitFilesAsync(root, cancellationToken))
            {
                if (IsInfrastructurePath(relativePath) || !includePatterns.Any(pattern => pattern.IsMatch(relativePath)))
                {
                    continue;
                }

                count++;
                if (!index.BySubject.TryGetValue(relativePath, out var metadata))
                {
                    // The subject's own sidecar path is the only attribution left once its content
                    // cannot be trusted, so an unreadable sidecar there marks the subject invalid.
                    var conventional = ReviewMetaPath.ForFile(root, relativePath, options.ReviewKind);
                    yield return index.Unreadable.Contains(conventional)
                        ? new FileStaleness(relativePath, StalenessState.Invalid, options.ReviewKind,
                            ReviewMetaPath.Describe(root, conventional))
                        : new FileStaleness(relativePath, StalenessState.Missing, options.ReviewKind);
                    continue;
                }

                var state = await EvaluateMetadataAsync(root, metadata, options, cancellationToken).ConfigureAwait(false);
                yield return new FileStaleness(relativePath, state, options.ReviewKind, metadata.MetaRelativePath);
            }
        }
        finally
        {
            QualityStudioEventSource.Log.ScanCompleted(root, count, stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<StalenessState> EvaluateMetadataAsync(
        string root,
        ReviewMetadata metadata,
        StalenessEvaluatorOptions options,
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
        if (!string.Equals(currentHash, metadata.ReviewedHash, StringComparison.Ordinal)) return StalenessState.Stale;
        if (metadata.ReviewInputHash is null) return StalenessState.Fresh;
        var inputs = inputResolver.Resolve(root, metadata.Kind, metadata.Level,
            options.GlobalInputsDirectory, options.InputBudgetCharacters,
            RuleCatalogueResolver.AdapterFromUnitId(metadata.UnitId));
        // The writer hashes the template of the reviewed level; module and project sidecars would
        // otherwise read as policy drift forever against the file template.
        var currentInputHash = inputs.EffectiveHash(ReviewPromptBuilder.TemplateHash(metadata.Level, metadata.Kind));
        return string.Equals(currentInputHash, metadata.ReviewInputHash, StringComparison.Ordinal)
            ? StalenessState.Fresh
            : StalenessState.PolicyDrift;
    }

    /// <summary>
    /// Indexes the project's sidecars from its data root. They used to be found by filtering the
    /// tracked files of the checkout, which stopped finding anything once sidecars left it.
    /// <para>
    /// Synchronous, and named so. Reading the sidecar lane is a directory walk and a parse per
    /// file with nothing to await; wrapping that in a task the caller awaits would only claim a
    /// yield that never happens. The one caller offloads it.
    /// </para>
    /// </summary>
    private static MetadataIndex LoadMetadata(
        string root,
        string reviewKind,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ReviewMetadata>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var absolutePath in ReviewMetaPath.Enumerate(root, reviewKind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ReviewMetaPath.Describe(root, absolutePath);
            if (!ReviewMetaReader.TryLoad(absolutePath, out var sidecar, out var error))
            {
                // A scan records the sidecars it cannot trust instead of failing whole; the
                // affected subjects come back as `invalid`, and the rest of the scan still runs.
                _ = error;
                unreadable.Add(absolutePath);
                continue;
            }

            var document = sidecar.Document;
            if (!string.Equals(document.Kind.ToString().ToLowerInvariant(), reviewKind, StringComparison.Ordinal) ||
                document.Unit.Level != ReviewLevel.File)
            {
                continue;
            }

            var metadata = new ReviewMetadata(
                NormalizeRelativePath(document.Unit.Path),
                document.Unit.Id,
                document.ReviewedHash.Value,
                NormalizeRelativePath(relativePath),
                document.SubjectInputs
                    .Select(input => new StoredSubjectInput(NormalizeRelativePath(input.Path), input.Selector))
                    .ToArray(),
                reviewKind,
                document.Unit.Level,
                document.ReviewInputs.EffectiveHash.Value);
            if (!result.TryAdd(metadata.SubjectPath, metadata))
            {
                throw new StalenessScanException(
                    $"Multiple '{reviewKind}' review metadata files target '{metadata.SubjectPath}'.");
            }
        }

        return new MetadataIndex(result, unreadable);
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
                CreateNoWindow = true,
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
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!absolutePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new StalenessScanException($"Review subject escapes the repository: {relativePath}");
        }

        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, absolutePath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new StalenessScanException("Review subjects cannot traverse symbolic links or junctions.");
        }

        return absolutePath;
    }

    // Any sidecar left in the checkout by a pre-data-root run sits below a `.quality` segment, so
    // the folder check covers it and no separate sidecar predicate is needed here.
    private static bool IsInfrastructurePath(string path) =>
        path.Split('/').Any(segment => segment is ".quality" or ".git" or "bin" or "obj");

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

    private sealed record MetadataIndex(
        IReadOnlyDictionary<string, ReviewMetadata> BySubject,
        IReadOnlySet<string> Unreadable);

    private sealed record StoredSubjectInput(string Path, string Selector);

    private sealed record ReviewMetadata(
        string SubjectPath,
        string UnitId,
        string ReviewedHash,
        string MetaRelativePath,
        IReadOnlyList<StoredSubjectInput> Inputs,
        string Kind,
        ReviewLevel Level,
        string? ReviewInputHash);
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

    [Event(6, Level = EventLevel.Informational)]
    public void InputsResolved(string filePath, string kind, int inputCount, int omissionCount,
        int includedCharacters, int budgetCharacters) =>
        WriteEvent(6, filePath, kind, inputCount, omissionCount, includedCharacters, budgetCharacters);

    [Event(7, Level = EventLevel.Informational)]
    public void SecurityScanStarted(string repositoryRoot, string mode) => WriteEvent(7, repositoryRoot, mode);

    [Event(8, Level = EventLevel.Informational)]
    public void SecurityScanCompleted(string repositoryRoot, string mode, string verdict, int fileCount,
        int newFindingCount, int acceptedFindingCount, long elapsedMilliseconds) =>
        WriteEvent(8, repositoryRoot, mode, verdict, fileCount, newFindingCount, acceptedFindingCount, elapsedMilliseconds);

    [Event(9, Level = EventLevel.Error)]
    public void SecurityScanUnavailable(string repositoryRoot, string mode, string errorType, string message) =>
        WriteEvent(9, repositoryRoot, mode, errorType, message);

    [Event(10, Level = EventLevel.Informational)]
    public void UsageRecorded(string runId, string path, string kind, long inputTokens, long outputTokens,
        long cachedInputTokens, long durationMs) =>
        WriteEvent(10, runId, path, kind, inputTokens, outputTokens, cachedInputTokens, durationMs);

    [Event(11, Level = EventLevel.Warning)]
    public void RuleIdRejected(string ruleId, string kind, string replacement) =>
        WriteEvent(11, ruleId, kind, replacement);

    [Event(12, Level = EventLevel.Error)]
    public void ReviewMetaUnreadable(string source, string failure, string reason) =>
        WriteEvent(12, source, failure, reason);
}
