using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Checks an explicit, versioned repository layout contract. No layout convention is
/// inferred for repositories which have not opted in. Dependency edges are checked by
/// language-native analyzers (for example ESLint) and enter reviews through SARIF.
/// </summary>
public sealed class ArchitectureSensor : IDeterministicEvidenceSensor
{
    public const string ContractFileName = "quality-architecture.json";
    public const string SensorVersion = "1.0.0";
    private const int MaximumContractBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".quality", ".quality-studio", "node_modules", "bin", "obj", "dist",
        "out-tsc", "coverage", ".angular", "TestResults",
    };

    public string Id => "architecture";
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public static bool HasTarget(string repositoryRoot) =>
        File.Exists(Path.Combine(repositoryRoot, ContractFileName));

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: Versions()));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        if (request.Scope != SensorScope.Repository)
            return Result(request, false, "Architecture layout checks require repository scope.", []);
        if (!HasTarget(root))
            return Result(request, false, $"No repository architecture contract ({ContractFileName}) is configured.", []);

        try
        {
            var contractPath = ContainedPath(root, ContractFileName);
            if (new FileInfo(contractPath).Length > MaximumContractBytes)
                throw new InvalidDataException($"Architecture contract exceeds {MaximumContractBytes} bytes.");
            var content = await File.ReadAllTextAsync(contractPath, cancellationToken).ConfigureAwait(false);
            var contract = JsonSerializer.Deserialize<ArchitectureContract>(content, JsonOptions)
                ?? throw new InvalidDataException("Architecture contract must be a JSON object.");
            Validate(root, contract);
            var findings = new List<ReviewFinding>();
            foreach (var path in contract.RequiredDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(ContainedPath(root, path)))
                    findings.Add(Finding("missing-directory", ContractFileName, path,
                        $"Required architecture directory is missing: {path}",
                        $"The repository contract requires the directory '{path}', but it does not exist.",
                        $"Restore '{path}' or update {ContractFileName} as part of an intentional architecture change."));
            }
            foreach (var path in contract.ForbiddenSourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = FirstSource(ContainedPath(root, path), cancellationToken);
                if (source is not null)
                {
                    var relative = Relative(root, source);
                    findings.Add(Finding("forbidden-source-path", relative, path,
                        $"Source code returned to a retired architecture path: {path}",
                        $"'{relative}' is inside '{path}', which {ContractFileName} reserves as a retired source location. " +
                        "Generated build output and Quality Studio metadata are ignored.",
                        "Move the source into the declared backend/frontend layout and update its references."));
                }
            }
            foreach (var rule in contract.DirectoryRules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = ContainedPath(root, rule.Path);
                if (!Directory.Exists(directory)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (IgnoredDirectories.Contains(name)) continue;
                    var isDirectory = Directory.Exists(entry);
                    var allowed = isDirectory ? rule.AllowedDirectories : rule.AllowedFiles;
                    if (allowed.Contains(name, StringComparer.Ordinal)) continue;
                    // A finding points to the real source when possible, so file reviews can
                    // receive the same deterministic evidence as repository reviews.
                    var source = isDirectory ? FirstSource(entry, cancellationToken) : entry;
                    if (source is null) continue;
                    var relative = Relative(root, source);
                    findings.Add(Finding("unexpected-entry", relative, Relative(root, entry),
                        $"Unexpected architecture entry: {Relative(root, entry)}",
                        $"'{rule.Path}' permits {string.Join(", ", rule.AllowedDirectories.Select(value => value + "/").Concat(rule.AllowedFiles))}. " +
                        $"'{name}' is outside that declared ownership boundary.",
                        "Move this source into its owning layer or update the architecture contract with the intended boundary."));
                }
            }
            return Result(request, true, null, findings);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            // Invalid configuration is visible evidence, never a clean scan or an implicit
            // adoption of defaults. The error points to the version-controlled contract.
            return Result(request, true, null,
                [Finding("invalid-contract", ContractFileName, ContractFileName,
                    "Repository architecture contract is invalid", exception.Message,
                    $"Correct {ContractFileName} using schemas/architecture-contract.v1.schema.json.")]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(request, false, $"Architecture scan could not read the repository: {exception.Message}", []);
        }
    }

    private static void Validate(string root, ArchitectureContract contract)
    {
        if (contract.SchemaVersion != 1)
            throw new InvalidDataException("Architecture contract schemaVersion must be 1.");
        if (contract.RequiredDirectories is null || contract.ForbiddenSourcePaths is null || contract.DirectoryRules is null)
            throw new InvalidDataException("Architecture contract lists must not be null.");
        if (contract.RequiredDirectories.Count + contract.ForbiddenSourcePaths.Count + contract.DirectoryRules.Count > 100)
            throw new InvalidDataException("Architecture contract allows at most 100 path rules.");
        foreach (var path in contract.RequiredDirectories.Concat(contract.ForbiddenSourcePaths))
            _ = ContainedPath(root, path);
        foreach (var rule in contract.DirectoryRules)
        {
            if (rule is null || rule.AllowedDirectories is null || rule.AllowedFiles is null)
                throw new InvalidDataException("Architecture directory rules and their allowlists must not be null.");
            _ = ContainedPath(root, rule.Path);
            foreach (var child in rule.AllowedDirectories.Concat(rule.AllowedFiles))
                if (string.IsNullOrWhiteSpace(child) || child is "." or ".." || child.IndexOfAny(['/', '\\', ':']) >= 0)
                    throw new InvalidDataException("Architecture allowlists must contain direct child names, not paths.");
        }
    }

    private static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Contains(':') || relative.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Architecture paths must be normalized repository-relative paths without traversal.");
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Architecture paths must not traverse symbolic links: {relative}");
        }
        return current;
    }

    private static string? FirstSource(string target, CancellationToken cancellationToken)
    {
        if (!Path.Exists(target)) return null;
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Architecture source inspection must not traverse symbolic links.");
        if (File.Exists(target)) return target;
        var pending = new Stack<string>();
        pending.Push(target);
        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (++visited > 10_000)
                throw new IOException("Architecture source inspection exceeded 10000 directories.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IgnoredDirectories.Contains(Path.GetFileName(entry))) continue;
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Architecture source inspection must not traverse symbolic links.");
                if (File.Exists(entry)) return entry;
                pending.Push(entry);
            }
        }
        return null;
    }

    private ReviewFinding Finding(string rule, string location, string identity, string title, string description, string recommendation)
    {
        var ruleId = "architecture/" + rule;
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Id}\0{ruleId}\0{identity}")));
        return new ReviewFinding(
            $"architecture-{fingerprint[^12..]}", "maintainability", FindingSeverity.Medium,
            title, description, recommendation, [new FindingLocation(location)], fingerprint, ruleId,
            Evidence: $"Repository-owned contract: {ContractFileName}",
            Source: new FindingSource(FindingSourceKind.Deterministic, Id, "Quality Studio architecture", Version));
    }

    private SensorScanResult Result(SensorScanRequest request, bool available, string? reason, IReadOnlyList<ReviewFinding> findings) =>
        new(available, reason, findings, new SensorProvenance(Id, Version, "repository", ".",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Versions()));

    private static IReadOnlyDictionary<string, string> Versions() =>
        new Dictionary<string, string> { ["architecture"] = SensorVersion };
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

public sealed record ArchitectureContract(
    int SchemaVersion,
    IReadOnlyList<string> RequiredDirectories,
    IReadOnlyList<string> ForbiddenSourcePaths,
    IReadOnlyList<ArchitectureDirectoryRule> DirectoryRules,
    [property: JsonPropertyName("$schema")] string? Schema = null);

public sealed record ArchitectureDirectoryRule(
    string Path,
    IReadOnlyList<string> AllowedDirectories,
    IReadOnlyList<string> AllowedFiles);
