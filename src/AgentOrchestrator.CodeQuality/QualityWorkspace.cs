using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The two roots a run works with: the analysed checkout, and the data root where everything the
/// studio derives about that checkout is written.
/// <para>
/// The studio used to write its outputs into the checkout it was analysing. When the analysed
/// project is also a Git working copy someone else integrates - Quality Studio analysing its own
/// repository is the case that surfaced it - every run left the tree dirty and the integration
/// refused to fast-forward it. Derived data now lands beside the checkout instead of inside it.
/// </para>
/// <para>
/// This is not a general "nothing may be written to the checkout" rule. Human-authored inputs
/// (<c>.quality/scope.json</c>, <c>.quality/rules/overrides.json</c>, <c>.quality/inputs/</c>,
/// the gitleaks configuration and baseline) stay repository-owned and are read from the checkout;
/// see <see cref="InRepository"/>. Only data a run derives is relocated.
/// </para>
/// </summary>
public sealed class QualityWorkspace
{
    /// <summary>Overrides the base directory that holds every project's data root.</summary>
    public const string DataRootVariable = "QUALITY_DATA_ROOT";

    /// <summary>The directory name a project's derived data lives under, inside the data root.</summary>
    public const string ProjectsDirectoryName = "projects";

    /// <summary>Records which checkout a data root belongs to, so an orphaned one can be traced back.</summary>
    public const string DescriptorFileName = "project.json";

    private QualityWorkspace(string repositoryRoot, string projectId, string dataRoot)
    {
        RepositoryRoot = repositoryRoot;
        ProjectId = projectId;
        DataRoot = dataRoot;
    }

    /// <summary>The analysed checkout. The studio reads its source and its authored inputs from here.</summary>
    public string RepositoryRoot { get; }

    /// <summary>
    /// Stable identity of the analysed checkout: a readable slug of the directory name plus a digest
    /// of its canonical path. The path is the key rather than the Git remote because two worktrees of
    /// one repository are two projects with different content, and they must not share a data root.
    /// </summary>
    public string ProjectId { get; }

    /// <summary>Where this project's derived data is written. Replaces the in-tree <c>.quality</c>.</summary>
    public string DataRoot { get; }

    /// <summary>
    /// Resolves the data root for a checkout from the environment: <see cref="DataRootVariable"/> when
    /// set, otherwise <c>&lt;LocalApplicationData&gt;/QualityStudio</c>.
    /// </summary>
    public static QualityWorkspace ForRepository(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Canonical(repositoryRoot);
        var projectId = IdentityOf(root);
        return new QualityWorkspace(root, projectId,
            Path.Combine(DefaultBaseDirectory(), ProjectsDirectoryName, projectId));
    }

    /// <summary>
    /// A workspace with an explicitly chosen data root, for a host that configures one and for tests
    /// that must not reach the developer's real data directory.
    /// </summary>
    public static QualityWorkspace At(string repositoryRoot, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var root = Canonical(repositoryRoot);
        return new QualityWorkspace(root, IdentityOf(root), Path.GetFullPath(dataRoot));
    }

    /// <summary>The base directory holding every project's data root, before the project segment.</summary>
    public static string DefaultBaseDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DataRootVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        // LocalApplicationData is %LOCALAPPDATA% on Windows and $XDG_DATA_HOME (or ~/.local/share)
        // elsewhere. It is per-user and not roamed, which matches data that is a cache of a local
        // checkout: it is rebuildable, machine-local, and must not follow the user to another host.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        // A stripped container image can leave the folder unresolved; falling back to the temp
        // directory keeps a run working rather than writing to the filesystem root.
        if (string.IsNullOrWhiteSpace(local)) local = Path.Combine(Path.GetTempPath(), "QualityStudio-data");
        return Path.Combine(local, "QualityStudio");
    }

    /// <summary>A path below the data root, where a caller used to combine below <c>.quality</c>.</summary>
    public string Combine(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Path.Combine([DataRoot, .. segments.Select(segment => segment.Replace('/', Path.DirectorySeparatorChar))]);
    }

    /// <summary>
    /// A path below the analysed checkout, for the authored inputs that stay repository-owned. Callers
    /// use this deliberately: it is the exception, and every use is a read of something a human wrote.
    /// </summary>
    public string InRepository(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Path.Combine([RepositoryRoot, .. segments.Select(segment => segment.Replace('/', Path.DirectorySeparatorChar))]);
    }

    /// <summary>Creates the data root and records which checkout it describes.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        var descriptor = Path.Combine(DataRoot, DescriptorFileName);
        if (File.Exists(descriptor)) return;
        // Written once and never rewritten: a moved checkout gets a new identity and therefore a new
        // data root, so rewriting this would only ever record a path that no longer maps here.
        AtomicFile.WriteAllText(descriptor,
            $"{{\n  \"projectId\": {System.Text.Json.JsonSerializer.Serialize(ProjectId)},\n" +
            $"  \"repositoryRoot\": {System.Text.Json.JsonSerializer.Serialize(RepositoryRoot)}\n}}\n");
    }

    private static string Canonical(string repositoryRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));

    private static string IdentityOf(string canonicalRoot)
    {
        // Windows and macOS default to case-insensitive filesystems, so the same checkout reached
        // through a differently-cased path must resolve to one project rather than two data roots.
        var key = OperatingSystem.IsLinux() ? canonicalRoot : canonicalRoot.ToLowerInvariant();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        var name = Path.GetFileName(canonicalRoot);
        var slug = Slug(name);
        return slug.Length == 0 ? digest : $"{slug}-{digest}";
    }

    /// <summary>A filesystem-safe, readable stand-in for the directory name, so the data root can be browsed.</summary>
    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length >= 32) break;
        }
        return builder.ToString().Trim('-');
    }
}
