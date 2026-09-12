namespace AgentOrchestrator.CodeQuality;

/// <summary>What one artefact's migration did, named so a report can explain itself.</summary>
public enum MigrationOutcome
{
    /// <summary>Moved out of the checkout into the data root.</summary>
    Moved,

    /// <summary>Nothing of this artefact was in the checkout.</summary>
    Absent,

    /// <summary>The data root already holds this artefact; the copy in the checkout was left alone.</summary>
    AlreadyPresent,

    /// <summary>The move failed. <see cref="MigratedArtefact.Error"/> says why; the rest still ran.</summary>
    Failed,
}

public sealed record MigratedArtefact(
    string RelativePath, MigrationOutcome Outcome, int Files, string? Error = null);

/// <summary>
/// What a migration did or, in a dry run, would do. <see cref="RemainingInTree"/> names what is
/// still in the checkout afterwards, so a caller never has to infer it from the absence of an entry.
/// </summary>
public sealed record QualityDataMigrationReport(
    string RepositoryRoot,
    string DataRoot,
    bool DryRun,
    IReadOnlyList<MigratedArtefact> Artefacts,
    IReadOnlyList<string> RemainingInTree)
{
    public int MovedFiles => Artefacts.Where(item => item.Outcome == MigrationOutcome.Moved).Sum(item => item.Files);

    public bool MovedAnything => MovedFiles > 0;

    /// <summary>The artefacts that could not be moved, if any. An empty list means a clean run.</summary>
    public IReadOnlyList<MigratedArtefact> Failures =>
        Artefacts.Where(item => item.Outcome == MigrationOutcome.Failed).ToArray();
}

/// <summary>
/// Moves the artefacts a pre-data-root studio wrote into the analysed checkout to that project's
/// data root. Run once per checkout; it is idempotent, and it refuses to overwrite an artefact the
/// data root already holds rather than choosing for the operator which copy is the real one.
/// </summary>
public static class QualityDataMigration
{
    /// <summary>
    /// Never follows a reparse point. A checkout may symlink an input folder at a directory outside
    /// itself; walking into one would move or delete files that are not this project's to touch.
    /// </summary>
    private static readonly EnumerationOptions Confined = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static QualityDataMigrationReport Run(string repositoryRoot, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var dataRoot = QualityDataRoot.For(root);
        var artefacts = new List<MigratedArtefact>();

        foreach (var artefact in QualityDataRoot.GeneratedArtefacts)
        {
            var segments = artefact.RelativePath.Split('/');
            var source = Path.Combine([root, QualityDataRoot.LegacyDirectoryName, .. segments]);
            var destination = Path.Combine([dataRoot, .. segments]);
            // One artefact's failure is reported and the rest still run. A migration that threw
            // halfway would leave the checkout partly moved with nothing to say what had already
            // gone - the worst possible state to hand an operator.
            try
            {
                artefacts.Add(artefact.RelativePath == ReviewMetaPath.LaneRoot
                    ? MigrateSidecars(root, destination, dryRun)
                    : Migrate(artefact, source, destination, dryRun));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                artefacts.Add(new MigratedArtefact(
                    artefact.RelativePath, MigrationOutcome.Failed, 0, exception.Message));
            }
        }

        return new QualityDataMigrationReport(
            root, dataRoot, dryRun, artefacts, RemainingInTree(root));
    }

    private static MigratedArtefact Migrate(
        GeneratedArtefact artefact, string source, string destination, bool dryRun)
    {
        var exists = artefact.IsDirectory ? Directory.Exists(source) : File.Exists(source);
        if (!exists) return new MigratedArtefact(artefact.RelativePath, MigrationOutcome.Absent, 0);
        if (artefact.IsDirectory ? Directory.Exists(destination) : File.Exists(destination))
            return new MigratedArtefact(artefact.RelativePath, MigrationOutcome.AlreadyPresent, 0);

        var files = artefact.IsDirectory
            ? Directory.EnumerateFiles(source, "*", Confined).Count()
            : 1;
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (artefact.IsDirectory) MoveDirectory(source, destination);
            else File.Move(source, destination);
        }

        return new MigratedArtefact(artefact.RelativePath, MigrationOutcome.Moved, files);
    }

    /// <summary>
    /// Moves a directory even when the destination is on another filesystem.
    /// <para>
    /// <see cref="Directory.Move"/> is a bare rename and fails with "invalid cross-device link" the
    /// moment the two paths are on different mounts - which is the shipped container layout, where
    /// the checkout is a bind mount and the data root is a named volume. <see cref="File.Move"/>
    /// already falls back to copy-and-delete; a directory has to do it by hand.
    /// </para>
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Falls through to copy-and-delete; a genuine failure resurfaces from there.
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", Confined))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", Confined))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }

        // Only once every file is readable at its destination is the source given up.
        Directory.Delete(source, recursive: true);
    }

    /// <summary>
    /// Sidecars are the one family that was never rooted at the checkout root: every reviewed
    /// folder had its own <c>.quality/reviews</c>. Each is re-rooted below the data root's single
    /// sidecar lane at the same relative position, which is exactly what
    /// <see cref="ReviewMetaPath"/> now computes for a fresh review of the same subject.
    /// </summary>
    private static MigratedArtefact MigrateSidecars(string root, string laneRoot, bool dryRun)
    {
        var moved = 0;
        var blocked = false;
        var drained = new List<string>();
        foreach (var lane in EnumerateLegacyLanes(root))
        {
            var owner = Path.GetDirectoryName(Path.GetDirectoryName(lane))!;
            var relativeOwner = Path.GetRelativePath(root, owner);
            foreach (var sidecar in Directory.EnumerateFiles(lane, "*.json", Confined))
            {
                var destination = Path.Combine(
                    laneRoot,
                    relativeOwner == "." ? string.Empty : relativeOwner,
                    Path.GetRelativePath(lane, sidecar));
                if (File.Exists(destination))
                {
                    blocked = true;
                    continue;
                }

                if (!dryRun)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(sidecar, destination);
                }

                moved++;
            }

            drained.Add(lane);
        }

        if (!dryRun) RemoveDrainedLanes(root, drained);
        return new MigratedArtefact(
            ReviewMetaPath.LaneRoot,
            moved > 0 ? MigrationOutcome.Moved : blocked ? MigrationOutcome.AlreadyPresent : MigrationOutcome.Absent,
            moved);
    }

    /// <summary>Every <c>&lt;folder&gt;/.quality/reviews</c> a pre-data-root run created in the checkout.</summary>
    private static IEnumerable<string> EnumerateLegacyLanes(string root) =>
        Directory.EnumerateDirectories(root, ReviewMetaPath.LaneRoot, Confined)
            .Where(path => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                QualityDataRoot.LegacyDirectoryName,
                StringComparison.Ordinal))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains(".git"))
            .ToArray();

    /// <summary>
    /// Removes the shells the moved sidecars left behind: each drained lane, and the
    /// <c>.quality</c> that held it once nothing else is in there.
    /// <para>
    /// Deliberately scoped to the lanes this run emptied rather than to every <c>.quality</c> in the
    /// tree. A blanket sweep would also delete an empty folder the author put there on purpose - a
    /// reserved <c>.quality/inputs/</c>, or the <c>.quality/preflight/</c> that stays in the
    /// checkout by design - and report nothing about having done so.
    /// </para>
    /// </summary>
    private static void RemoveDrainedLanes(string root, IEnumerable<string> lanes)
    {
        foreach (var lane in lanes)
        {
            // Deepest first, so `files`/`namespaces` go before the lane that contains them.
            var directories = Directory.EnumerateDirectories(lane, "*", Confined)
                .Append(lane)
                .OrderByDescending(path => path.Length);
            foreach (var directory in directories) DeleteIfEmpty(directory);

            var legacy = Path.GetDirectoryName(lane)!;
            if (PathConfinedToRoot(root, legacy)) DeleteIfEmpty(legacy);
        }
    }

    private static bool PathConfinedToRoot(string root, string path) =>
        Path.GetRelativePath(root, path) is var relative &&
        !relative.StartsWith("..", StringComparison.Ordinal) &&
        !Path.IsPathRooted(relative);

    private static void DeleteIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder another process is holding stays; it is empty and harmless either way.
        }
    }

    /// <summary>
    /// The generated artefacts still under the checkout's <c>.quality</c> after a migration, as
    /// checkout-relative paths. Empty is the state the Agent Studio integration needs.
    /// </summary>
    public static IReadOnlyList<string> RemainingInTree(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var remaining = new List<string>();
        foreach (var artefact in QualityDataRoot.GeneratedArtefacts)
        {
            if (artefact.RelativePath == ReviewMetaPath.LaneRoot)
            {
                remaining.AddRange(EnumerateLegacyLanes(root)
                    .Where(lane => Directory.EnumerateFiles(lane, "*", Confined).Any())
                    .Select(lane => Relative(root, lane)));
                continue;
            }

            var path = Path.Combine(
                [root, QualityDataRoot.LegacyDirectoryName, .. artefact.RelativePath.Split('/')]);
            if (artefact.IsDirectory ? Directory.Exists(path) : File.Exists(path))
                remaining.Add(Relative(root, path));
        }

        return remaining.Order(StringComparer.Ordinal).ToArray();
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
