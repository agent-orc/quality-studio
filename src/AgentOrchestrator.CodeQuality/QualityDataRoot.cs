using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>One generated artefact of a project, addressed relative to its data root.</summary>
public sealed record GeneratedArtefact(string RelativePath, bool IsDirectory);

/// <summary>
/// Where everything a studio run produces for one analysed working copy is kept.
/// <para>
/// The analysed checkout is the subject of a review, not its filing cabinet. Writing ledgers,
/// reports and review sidecars back into it made the studio's own repository permanently dirty and
/// blocked Agent Studio, which refuses to fast-forward a checkout with uncommitted changes. Every
/// generated artefact therefore lives under a per-project directory outside the checkout, and the
/// checkout stays read-only for the studio apart from explicit, user-triggered exports.
/// </para>
/// <para>
/// Author-owned inputs are the deliberate exception and keep living in the checkout, because they
/// are hand-written configuration that belongs to the analysed project and is meant to be reviewed
/// and versioned with it: <c>.quality/scope.json</c>, <c>.quality/inputs/</c>,
/// <c>.quality/rules/overrides.json</c>, <c>.quality/security/</c> and
/// <c>.quality/attacks/catalogue.json</c>. See <c>docs/data-root.md</c> for the full contract.
/// </para>
/// </summary>
public static class QualityDataRoot
{
    /// <summary>Overrides the base directory for a whole host or test run.</summary>
    public const string EnvironmentVariable = "QUALITY_STUDIO_DATA_ROOT";

    /// <summary>
    /// The environment spelling of the API's <c>QualityStudio:DataRoot</c> setting, read here so a
    /// process with no ASP.NET configuration honours it too. Without this the CLI and the host
    /// disagree inside a container: the image sets only <c>QualityStudio__DataRoot</c>, so
    /// <c>quality migrate-data</c> - the command the host's own startup warning tells an operator to
    /// run - would move the data somewhere the host never looks, and lose it with the container.
    /// </summary>
    public const string HostConfigurationEnvironmentVariable = "QualityStudio__DataRoot";

    /// <summary>The product's folder below the per-user application data directory.</summary>
    public const string ProductDirectoryName = "QualityStudio";

    /// <summary>The folder that holds one directory per analysed project.</summary>
    public const string ProjectsDirectoryName = "projects";

    /// <summary>
    /// What a studio run generates, relative to the data root, and therefore what the migration
    /// moves out of the checkout. Named one artefact at a time rather than one folder at a time
    /// because <c>attacks/</c> holds both: the catalogue is authored, the ledger is generated.
    /// <para>
    /// Everything absent from this list stays in the checkout on purpose - author-owned inputs
    /// because they belong to the analysed project, and <c>.quality/preflight</c> because an
    /// external analyzer writes it under the working directory it runs in and the analyzer command
    /// confines that path to the checkout, a guard worth more than the tidiness of moving it.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<GeneratedArtefact> GeneratedArtefacts =
    [
        new("findings", IsDirectory: true),
        new("reports", IsDirectory: true),
        new("usage", IsDirectory: true),
        new("reviews", IsDirectory: true),
        new("boundaries", IsDirectory: true),
        new("coverage", IsDirectory: true),
        new("changes", IsDirectory: true),
        new("flows", IsDirectory: true),
        new("runs", IsDirectory: true),
        new("attacks/coverage-ledger.jsonl", IsDirectory: false),
    ];

    /// <summary>
    /// Resolved data roots, keyed by base directory and checkout together. Keying on the checkout
    /// alone would keep serving a root resolved under a base that has since changed - which the
    /// environment variable can do without going through <see cref="Configure"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Base, string Root), string> Resolved =
        new();

    private static volatile string? configuredBase;

    /// <summary>
    /// Points the studio at a base directory, ahead of the environment variable and the per-user
    /// default. A host calls this once while it builds; passing null or blank restores the default
    /// resolution.
    /// </summary>
    public static void Configure(string? baseDirectory) =>
        configuredBase = string.IsNullOrWhiteSpace(baseDirectory)
            ? null
            : Path.GetFullPath(baseDirectory);

    /// <summary>The directory that holds every project's data root, without creating it.</summary>
    public static string BaseDirectory
    {
        get
        {
            if (configuredBase is { } configured) return configured;
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(fromEnvironment))
                fromEnvironment = Environment.GetEnvironmentVariable(HostConfigurationEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return Path.GetFullPath(fromEnvironment);
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData))
                applicationData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(applicationData, ProductDirectoryName);
        }
    }

    /// <summary>
    /// The stable identity of one analysed working copy: a readable slug of its directory name and a
    /// digest of its canonical path. The path is the identity because two working copies of the same
    /// remote - a second clone, a Git worktree - are separately analysed projects with separate
    /// findings, and must not share one ledger.
    /// </summary>
    public static string ProjectKey(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var canonical = Canonical(repositoryRoot);
        var name = new DirectoryInfo(canonical).Name;
        var slug = Slug(name);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical)))[..12];
        return slug.Length == 0 ? digest : slug + "-" + digest;
    }

    /// <summary>The data root of one analysed working copy, without creating it.</summary>
    public static string For(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Resolved.GetOrAdd(
            (BaseDirectory, Canonical(repositoryRoot)),
            static key => Path.Combine(key.Base, ProjectsDirectoryName, ProjectKey(key.Root)));
    }

    /// <summary>The path of a generated artefact of one analysed working copy, without creating it.</summary>
    public static string Combine(string repositoryRoot, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Path.Combine([For(repositoryRoot), .. segments]);
    }

    /// <summary>The folder the studio used to write into the analysed checkout.</summary>
    internal const string LegacyDirectoryName = ".quality";

    private static string Canonical(string repositoryRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));

    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length == 40) break;
        }

        return builder.ToString().Trim('-');
    }
}
