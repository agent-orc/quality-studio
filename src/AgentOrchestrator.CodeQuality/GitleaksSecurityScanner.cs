using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public class GitleaksSecurityScanner
{
    private readonly GitleaksBinaryResolver _resolver;
    private readonly HttpClient _httpClient;

    public GitleaksSecurityScanner(GitleaksBinaryResolver? resolver = null, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _resolver = resolver ?? new GitleaksBinaryResolver(_httpClient);
    }

    public virtual async Task<SecurityScanResult> ScanAsync(SecurityScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        var configPath = ResolveOptionalPath(root, request.ConfigPath, ".quality/security/gitleaks.toml");
        var baselinePath = ResolveOptionalPath(root, request.BaselinePath, ".quality/security/gitleaks.baseline.json");
        var stopwatch = Stopwatch.StartNew();
        SecurityScanOutput output;
        string binaryPath;
        QualityStudioEventSource.Log.SecurityScanStarted(root, request.Mode.ToString().ToLowerInvariant());
        try
        {
            binaryPath = await _resolver.ResolveAsync(
                Environment.GetEnvironmentVariable("QUALITY_GITLEAKS_PATH"),
                cancellationToken).ConfigureAwait(false);
            output = await RunScanAsync(root, request, binaryPath, configPath, baselinePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SecurityScannerUnavailableException or IOException or InvalidOperationException or Win32Exception or JsonException)
        {
            var unavailableReason = GetUnavailableReason(exception);
            var unavailable = new SecurityScanReport(
                SecurityVerdict.Unavailable,
                Available: false,
                Scanner: "gitleaks",
                Version: GitleaksBinaryResolver.PinnedVersion,
                Mode: request.Mode.ToString().ToLowerInvariant(),
                Range: request.Range,
                ConfigPath: configPath,
                BaselinePath: baselinePath,
                ScannedAt: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                FilesScanned: 0,
                NewFindings: 0,
                AcceptedFindings: 0,
                BlockFindings: 0,
                WarnFindings: 0,
                CleanFiles: 0,
                UnavailableReason: unavailableReason,
                Findings: Array.Empty<SecurityFindingRecord>());
            QualityStudioEventSource.Log.SecurityScanUnavailable(root, request.Mode.ToString().ToLowerInvariant(), exception.GetType().Name, unavailableReason);
            return new SecurityScanResult(
                unavailable,
                new SecurityScanProvenance("gitleaks", GitleaksBinaryResolver.PinnedVersion,
                    request.Mode.ToString().ToLowerInvariant(), request.Range, configPath, baselinePath,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                new SecurityScanCounts(0, 0, 0, 0, 0, 0),
                Array.Empty<SecurityFindingRecord>());
        }

        var baselineFingerprints = await LoadBaselineFingerprintsAsync(baselinePath, cancellationToken).ConfigureAwait(false);
        var findings = output.Findings.Select(finding =>
            baselineFingerprints.Contains(finding.Fingerprint)
                ? finding with { Accepted = true }
                : finding).ToArray();
        var grouped = findings.GroupBy(finding => finding.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (request.PersistMetadata)
        {
            await PersistFindingsAsync(root, binaryPath, request, grouped, configPath, baselinePath, cancellationToken)
                .ConfigureAwait(false);
        }

        var report = BuildReport(request, configPath, baselinePath, output.FilesScanned, grouped, findings);
        var provenance = new SecurityScanProvenance(
            "gitleaks",
            output.Version,
            request.Mode.ToString().ToLowerInvariant(),
            request.Range,
            configPath,
            baselinePath,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var counts = new SecurityScanCounts(
            report.FilesScanned,
            report.NewFindings,
            report.AcceptedFindings,
            report.BlockFindings,
            report.WarnFindings,
            report.CleanFiles);

        QualityStudioEventSource.Log.SecurityScanCompleted(
            root,
            report.Mode,
            report.Verdict.ToString().ToLowerInvariant(),
            report.FilesScanned,
            report.NewFindings,
            report.AcceptedFindings,
            stopwatch.ElapsedMilliseconds);
        return new SecurityScanResult(report, provenance, counts, findings);
    }

    private static SecurityScanReport BuildReport(
        SecurityScanRequest request,
        string? configPath,
        string? baselinePath,
        int filesScanned,
        IReadOnlyCollection<IGrouping<string, SecurityFindingRecord>> grouped,
        IReadOnlyList<SecurityFindingRecord> findings)
    {
        var newFindings = findings.Count(finding => !finding.Accepted);
        var acceptedFindings = findings.Count - newFindings;
        var blockFindings = findings.Count(finding => !finding.Accepted && finding.Severity is FindingSeverity.Critical or FindingSeverity.High);
        var warnFindings = findings.Count(finding => !finding.Accepted && finding.Severity is FindingSeverity.Medium or FindingSeverity.Low);
        var verdict = blockFindings > 0
            ? SecurityVerdict.Block
            : newFindings > 0
                ? SecurityVerdict.Warn
                : SecurityVerdict.Pass;
        var cleanFiles = Math.Max(0, filesScanned - grouped.Count);
        return new SecurityScanReport(
            verdict,
            Available: true,
            Scanner: "gitleaks",
            Version: GitleaksBinaryResolver.PinnedVersion,
            Mode: request.Mode.ToString().ToLowerInvariant(),
            Range: request.Range,
            ConfigPath: configPath,
            BaselinePath: baselinePath,
            ScannedAt: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            FilesScanned: filesScanned,
            NewFindings: newFindings,
            AcceptedFindings: acceptedFindings,
            BlockFindings: blockFindings,
            WarnFindings: warnFindings,
            CleanFiles: cleanFiles,
            UnavailableReason: null,
            Findings: findings);
    }

    private static async Task<HashSet<string>> LoadBaselineFingerprintsAsync(
        string? baselinePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baselinePath) || !File.Exists(baselinePath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var json = await File.ReadAllTextAsync(baselinePath, cancellationToken).ConfigureAwait(false);
        CollectBaselineFingerprints(JsonNode.Parse(json), fingerprints);

        return fingerprints;
    }

    private async Task PersistFindingsAsync(
        string root,
        string gitleaksPath,
        SecurityScanRequest request,
        IEnumerable<IGrouping<string, SecurityFindingRecord>> grouped,
        string? configPath,
        string? baselinePath,
        CancellationToken cancellationToken)
    {
        var hierarchyFiles = FlattenHierarchy(RepositoryHierarchyBuilder.Build(root))
            .Where(node => node.Level == ReviewLevel.File)
            .GroupBy(node => node.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);
        foreach (var fileGroup in grouped)
        {
            var relativePath = NormalizeRelativePath(fileGroup.Key);
            var absolutePath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolutePath.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(absolutePath, root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(absolutePath))
            {
                continue;
            }

            var fileContentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(absolutePath, cancellationToken)
                .ConfigureAwait(false);
            var acceptedFindings = fileGroup.Where(finding => finding.Accepted).ToArray();
            var newFindings = fileGroup.Where(finding => !finding.Accepted).ToArray();
            var hasFindings = newFindings.Length > 0 || acceptedFindings.Length > 0;
            if (!hasFindings)
            {
                continue;
            }

            var canonicalUnit = hierarchyFiles.GetValueOrDefault(relativePath);
            var adapter = canonicalUnit is null ? GetAdapter(absolutePath) : AdapterFromUnitId(canonicalUnit.Id);
            var unitId = canonicalUnit?.Id ?? $"qs-v1/{adapter}/file/{Sha256($"{adapter}\0{relativePath}")}";
            var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(
                unitId,
                [new SubjectInputHash(relativePath, "file", fileContentHash)]);
            var summary = newFindings.Length > 0
                ? $"{newFindings.Length} new secret detection(s) and {acceptedFindings.Length} accepted placeholder match(es) were recorded by Gitleaks."
                : $"{acceptedFindings.Length} accepted placeholder match(es) were recorded by Gitleaks.";
            var doc = new ReviewMetaDocument
            {
                Unit = new ReviewUnit(unitId, ParseAdapter(adapter), ReviewLevel.File, relativePath, Path.GetFileName(relativePath)),
                ReviewedAt = DateTimeOffset.UtcNow,
                Kind = ReviewKind.Security,
                Reviewer = new ReviewerIdentity("gitleaks", GitleaksBinaryResolver.PinnedVersion),
                ReviewedHash = ManifestHash.Subject(reviewedHash),
                SubjectInputs = [new SubjectInputHash(relativePath, "file", fileContentHash)],
                ReviewInputs = new ReviewInputs(
                    ManifestHash.ReviewInput(Sha256($"{request.Mode}\0{configPath ?? ""}\0{baselinePath ?? ""}\0{gitleaksPath}")),
                    true,
                    [],
                    [],
                    new PromptReference(
                        "gitleaks-security-scan",
                        GitleaksBinaryResolver.PinnedVersion,
                        "sha256:" + Sha256($"{request.Mode}\0{configPath ?? ""}\0{baselinePath ?? ""}\0{gitleaksPath}"))),
                Grade = BuildGrade(newFindings),
                Summary = summary,
                Aspects = [new ReviewAspect("secrets", "Secrets", BuildGrade(newFindings))],
                Findings = newFindings.Select(ToReviewFinding).ToArray(),
            };

            var metaPath = Path.Combine(Path.GetDirectoryName(absolutePath)!, ".quality", "reviews", "files", $"file.{Sha256(relativePath)}.review-meta.security.json");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            var temporaryPath = metaPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporaryPath, ReviewMetaJson.Serialize(doc) + Environment.NewLine, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, metaPath, true);
        }
    }

    private static ReviewFinding ToReviewFinding(SecurityFindingRecord finding) =>
        new(
            finding.Id,
            finding.Aspect,
            finding.Severity,
            finding.Title,
            finding.Description,
            finding.Recommendation,
            finding.Locations,
            finding.Fingerprint,
            finding.RuleId,
            finding.Evidence);

    private static ReviewGrade BuildGrade(IReadOnlyCollection<SecurityFindingRecord> findings)
    {
        if (findings.Count == 0)
        {
            return new ReviewGrade(100, GradeBand.A, "No new secrets were detected.");
        }

        if (findings.Any(finding => finding.Severity is FindingSeverity.Critical or FindingSeverity.High && !finding.Accepted))
        {
            return new ReviewGrade(0, GradeBand.F, "A high-confidence secret was detected.");
        }

        return new ReviewGrade(72, GradeBand.C, "Potential secrets were detected and should be triaged.");
    }

    private async Task<SecurityScanOutput> RunScanAsync(
        string root,
        SecurityScanRequest request,
        string gitleaksPath,
        string? configPath,
        string? baselinePath,
        CancellationToken cancellationToken,
        int? filesScannedOverride = null)
    {
        var scannedFiles = filesScannedOverride ?? request.Mode switch
        {
            SecurityScanMode.Repository => await CountRepositoryFilesAsync(root, cancellationToken).ConfigureAwait(false),
            SecurityScanMode.Range => await CountRangeFilesAsync(root, request.Range!, cancellationToken).ConfigureAwait(false),
            SecurityScanMode.Staged => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode), request.Mode, null),
        };

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(gitleaksPath)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--no-banner");
        process.StartInfo.ArgumentList.Add("--no-color");
        process.StartInfo.ArgumentList.Add("--redact");
        process.StartInfo.ArgumentList.Add("100");
        process.StartInfo.ArgumentList.Add("--report-format");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("--exit-code");
        process.StartInfo.ArgumentList.Add("1");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            process.StartInfo.ArgumentList.Add("--config");
            process.StartInfo.ArgumentList.Add(configPath);
        }
        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            process.StartInfo.ArgumentList.Add("--baseline-path");
            process.StartInfo.ArgumentList.Add(baselinePath);
        }

        switch (request.Mode)
        {
            case SecurityScanMode.Repository:
                process.StartInfo.ArgumentList.Add("dir");
                process.StartInfo.ArgumentList.Add(root);
                break;
            case SecurityScanMode.Range:
                process.StartInfo.ArgumentList.Add("git");
                process.StartInfo.ArgumentList.Add("--log-opts");
                process.StartInfo.ArgumentList.Add(request.Range ?? throw new ArgumentException("A git range is required for range scans.", nameof(request)));
                process.StartInfo.ArgumentList.Add(root);
                break;
            case SecurityScanMode.Staged:
                return await ScanStagedAsync(root, gitleaksPath, configPath, baselinePath, cancellationToken)
                    .ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Mode), request.Mode, null);
        }

        try
        {
            if (!process.Start())
            {
                throw new SecurityScannerUnavailableException("Gitleaks did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new SecurityScannerUnavailableException("Gitleaks could not be launched.", exception);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode is not (0 or 1))
        {
            throw new SecurityScannerUnavailableException($"Gitleaks exited with code {process.ExitCode}.");
        }

        return ParseOutput(stdout) with { FilesScanned = scannedFiles };
    }

    private async Task<SecurityScanOutput> ScanStagedAsync(
        string root,
        string gitleaksPath,
        string? configPath,
        string? baselinePath,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"quality-studio-staged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var stagedFiles = await ListStagedFilesAsync(root, cancellationToken).ConfigureAwait(false);
            var deletedFiles = await ListDeletedStagedFilesAsync(root, cancellationToken).ConfigureAwait(false);
            var scannedPaths = new HashSet<string>(stagedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in stagedFiles)
            {
                var content = await ReadIndexFileAsync(root, relativePath, cancellationToken).ConfigureAwait(false);
                var destination = Path.Combine(tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, content, cancellationToken).ConfigureAwait(false);
            }

            foreach (var relativePath in deletedFiles)
            {
                if (!scannedPaths.Add(relativePath))
                {
                    continue;
                }

                var content = await ReadDeletedFileAsync(root, relativePath, cancellationToken).ConfigureAwait(false);
                var destination = Path.Combine(tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, content, cancellationToken).ConfigureAwait(false);
            }

            var stagedRequest = new SecurityScanRequest(tempRoot, SecurityScanMode.Repository, null, configPath, baselinePath, false);
            var output = await RunScanAsync(tempRoot, stagedRequest, gitleaksPath, configPath, baselinePath, cancellationToken, scannedPaths.Count)
                .ConfigureAwait(false);
            return output;
        }
        finally
        {
            DeletePath(tempRoot);
        }
    }

    private static async Task<IReadOnlyList<string>> ListStagedFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = await RunGitAsync(root, cancellationToken, "diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z")
            .ConfigureAwait(false);
        return files.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeRelativePath)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ListDeletedStagedFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = await RunGitAsync(root, cancellationToken, "diff", "--cached", "--name-only", "--diff-filter=D", "-z")
            .ConfigureAwait(false);
        return files.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeRelativePath)
            .ToArray();
    }

    private static async Task<string> ReadIndexFileAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        return await RunGitAsync(root, cancellationToken, "show", $":{relativePath}")
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadDeletedFileAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        return await RunGitAsync(root, cancellationToken, "show", $"HEAD:{relativePath}")
            .ConfigureAwait(false);
    }

    private static async Task<int> CountRepositoryFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = await RunGitAsync(root, cancellationToken, "ls-files", "--cached", "--others", "--exclude-standard", "-z")
            .ConfigureAwait(false);
        return files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task<int> CountRangeFilesAsync(string root, string range, CancellationToken cancellationToken)
    {
        var files = await RunGitAsync(root, cancellationToken, "diff", "--name-only", "--diff-filter=ACMRD", "-z", range)
            .ConfigureAwait(false);
        return files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task<string> RunGitAsync(string root, CancellationToken cancellationToken, params string[] arguments)
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
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new SecurityScannerUnavailableException("Git did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new SecurityScannerUnavailableException("Git is required for staged scans.", exception);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new SecurityScannerUnavailableException($"Git command failed with exit code {process.ExitCode}.");
        }

        return stdout;
    }

    private static SecurityScanOutput ParseOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new SecurityScanOutput(Array.Empty<SecurityFindingRecord>(), GitleaksBinaryResolver.PinnedVersion, 0);
        }

        var trimmed = output.TrimStart();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return ParseJsonReport(output);
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return ParseSarifReport(output);
        }

        throw new JsonException("Unexpected Gitleaks report format.");
    }

    private static SecurityScanOutput ParseJsonReport(string json)
    {
        var findingNodes = JsonNode.Parse(json)?.AsArray() ?? new JsonArray();
        var findings = new List<SecurityFindingRecord>(findingNodes.Count);
        foreach (var node in findingNodes)
        {
            var finding = node?.AsObject();
            if (finding is null)
            {
                continue;
            }

            findings.Add(ParseJsonFinding(finding));
        }

        return new SecurityScanOutput(findings, ExtractVersion(findings), 0);
    }

    private static SecurityFindingRecord ParseJsonFinding(JsonObject finding)
    {
        var ruleId = finding["RuleID"]?.GetValue<string>() ?? finding["RuleId"]?.GetValue<string>() ?? "gitleaks";
        var path = NormalizeRelativePath(finding["File"]?.GetValue<string>() ?? string.Empty);
        var startLine = finding["StartLine"]?.GetValue<int>() ?? 1;
        var endLine = finding["EndLine"]?.GetValue<int>() ?? startLine;
        var startColumn = finding["StartColumn"]?.GetValue<int>() ?? 1;
        var endColumn = finding["EndColumn"]?.GetValue<int>() ?? startColumn;
        var severity = ParseSeverity(
            finding["Severity"]?.GetValue<string>() ??
            finding["RuleSeverity"]?.GetValue<string>() ??
            finding["severity"]?.GetValue<string>() ??
            finding["ruleSeverity"]?.GetValue<string>(),
            FindingSeverity.High);
        return BuildFinding(
            path,
            ruleId,
            severity,
            finding["Description"]?.GetValue<string>(),
            startLine,
            endLine,
            startColumn,
            endColumn,
            finding["Fingerprint"]?.GetValue<string>());
    }

    private static SecurityScanOutput ParseSarifReport(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("The Gitleaks SARIF report is invalid.");
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        var runs = root["runs"]?.AsArray() ?? new JsonArray();
        var findings = new List<SecurityFindingRecord>();
        foreach (var runNode in runs)
        {
            var run = runNode?.AsObject();
            if (run is null)
            {
                continue;
            }

            JsonArray? rulesArray = null;
            if (run["tool"] is JsonObject tool &&
                tool["driver"] is JsonObject driver &&
                driver["rules"] is JsonArray ruleNodes)
            {
                rulesArray = ruleNodes;
            }
            if (rulesArray is not null)
            {
                foreach (var ruleNode in rulesArray)
                {
                    var rule = ruleNode?.AsObject();
                    if (rule is null)
                    {
                        continue;
                    }

                    var id = rule["id"]?.GetValue<string>();
                    var description = rule["shortDescription"]?["text"]?.GetValue<string>() ?? rule["fullDescription"]?["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(description))
                    {
                        rules[id] = description;
                    }
                }
            }

            var results = run["results"]?.AsArray() ?? new JsonArray();
            foreach (var resultNode in results)
            {
                var result = resultNode?.AsObject();
                if (result is null)
                {
                    continue;
                }

                var ruleId = result["ruleId"]?.GetValue<string>() ?? "gitleaks";
                var location = result["locations"]?.AsArray().FirstOrDefault()?.AsObject();
                if (location is null)
                {
                    continue;
                }

                var physical = location["physicalLocation"]?.AsObject();
                var artifact = physical?["artifactLocation"]?.AsObject();
                var region = physical?["region"]?.AsObject();
                var path = NormalizeRelativePath(artifact?["uri"]?.GetValue<string>() ?? string.Empty);
                var severity = ParseSeverity(result["level"]?.GetValue<string>(), FindingSeverity.High);
                findings.Add(BuildFinding(
                    path,
                    ruleId,
                    severity,
                    rules.TryGetValue(ruleId, out var description) ? description : result["message"]?["text"]?.GetValue<string>(),
                    region?["startLine"]?.GetValue<int>() ?? 1,
                    region?["endLine"]?.GetValue<int>() ?? region?["startLine"]?.GetValue<int>() ?? 1,
                    region?["startColumn"]?.GetValue<int>() ?? 1,
                    region?["endColumn"]?.GetValue<int>() ?? region?["startColumn"]?.GetValue<int>() ?? 1,
                    ExtractFingerprint(result["partialFingerprints"]?.AsObject())));
            }
        }

        return new SecurityScanOutput(findings, ExtractVersion(findings), 0);
    }

    private static SecurityFindingRecord BuildFinding(
        string path,
        string ruleId,
        FindingSeverity severity,
        string? description,
        int startLine,
        int endLine,
        int startColumn,
        int endColumn,
        string? fingerprint)
    {
        var normalizedPath = NormalizeRelativePath(path);
        var id = $"gitleaks-{Sha256($"{ruleId}\0{normalizedPath}\0{startLine}\0{startColumn}\0{endLine}\0{endColumn}")[..16]}";
        var location = new FindingLocation(normalizedPath, new FindingRange(
            new FindingPosition(startLine, startColumn),
            new FindingPosition(endLine, endColumn)));
        var computedFingerprint = ComputeFingerprint(normalizedPath, ruleId, startLine, startColumn, endLine, endColumn);
        return new SecurityFindingRecord(
            id,
            "secrets",
            severity,
            description?.Length > 0 ? description : $"Gitleaks rule {ruleId} detected a potential secret.",
            $"Gitleaks detected a potential secret in {normalizedPath} at lines {startLine}-{endLine}.",
            "Remove the secret, rotate any exposed credential, and keep only a narrowly audited placeholder or baseline entry if this match is intentional.",
            [location],
            !string.IsNullOrWhiteSpace(fingerprint) ? fingerprint : computedFingerprint,
            ruleId,
            null,
            normalizedPath,
            Accepted: false);
    }

    private static string ExtractVersion(IReadOnlyCollection<SecurityFindingRecord> findings) =>
        GitleaksBinaryResolver.PinnedVersion;

    private static string? ResolveOptionalPath(string root, string? supplied, string relativeDefault)
    {
        var candidate = string.IsNullOrWhiteSpace(supplied)
            ? Path.Combine(root, relativeDefault.Replace('/', Path.DirectorySeparatorChar))
            : Path.IsPathRooted(supplied)
                ? supplied
                : Path.Combine(root, supplied);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string GetAdapter(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".fs" or ".vb" ? "dotnet" : "generic";

    private static string AdapterFromUnitId(string unitId) => unitId.Split('/')[1];

    private static ReviewAdapter ParseAdapter(string adapter) => adapter switch
    {
        "angular" => ReviewAdapter.Angular,
        "dotnet" => ReviewAdapter.Dotnet,
        "generic" => ReviewAdapter.Generic,
        _ => throw new ArgumentException($"Unsupported hierarchy adapter '{adapter}'."),
    };

    private static IEnumerable<HierarchyNode> FlattenHierarchy(IEnumerable<HierarchyNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in FlattenHierarchy(node.Children)) yield return child;
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ComputeFingerprint(string path, string ruleId, int startLine, int startColumn, int endLine, int endColumn) =>
        $"sha256:{Sha256($"gitleaks-finding-v1\0{ruleId}\0{path}\0{startLine}\0{startColumn}\0{endLine}\0{endColumn}")}";

    private static FindingSeverity ParseSeverity(string? value, FindingSeverity fallback) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "critical" or "error" => FindingSeverity.Critical,
            "high" => FindingSeverity.High,
            "medium" or "warning" or "warn" => FindingSeverity.Medium,
            "low" => FindingSeverity.Low,
            "info" or "note" => FindingSeverity.Info,
            _ => fallback,
        };

    private static string? ExtractFingerprint(JsonObject? node)
    {
        if (node is null)
        {
            return null;
        }

        var fingerprint = node["fingerprint"]?.GetValue<string>() ?? node["Fingerprint"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            return fingerprint;
        }

        return null;
    }

    private static void CollectBaselineFingerprints(JsonNode? node, HashSet<string> fingerprints)
    {
        if (node is null)
        {
            return;
        }

        switch (node)
        {
            case JsonArray array:
                foreach (var entry in array)
                {
                    CollectBaselineFingerprints(entry, fingerprints);
                }
                break;
            case JsonObject obj:
                AddBaselineFingerprint(obj, fingerprints);
                foreach (var property in obj)
                {
                    CollectBaselineFingerprints(property.Value, fingerprints);
                }
                break;
        }
    }

    private static void AddBaselineFingerprint(JsonObject finding, HashSet<string> fingerprints)
    {
        var fingerprint = finding["Fingerprint"]?.GetValue<string>() ?? finding["fingerprint"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            fingerprints.Add(fingerprint);
            return;
        }

        var path = NormalizeRelativePath(
            finding["File"]?.GetValue<string>() ??
            finding["file"]?.GetValue<string>() ??
            finding["path"]?.GetValue<string>() ??
            finding["uri"]?.GetValue<string>() ??
            string.Empty);
        var hasFindingShape =
            !string.IsNullOrWhiteSpace(path) &&
            (
                finding["RuleID"] is not null ||
                finding["RuleId"] is not null ||
                finding["ruleId"] is not null ||
                finding["StartLine"] is not null ||
                finding["startLine"] is not null ||
                finding["StartColumn"] is not null ||
                finding["startColumn"] is not null ||
                finding["EndLine"] is not null ||
                finding["endLine"] is not null ||
                finding["EndColumn"] is not null ||
                finding["endColumn"] is not null);
        if (!hasFindingShape)
        {
            return;
        }

        var ruleId = finding["RuleID"]?.GetValue<string>() ?? finding["RuleId"]?.GetValue<string>() ?? finding["ruleId"]?.GetValue<string>() ?? "gitleaks";
        var startLine = finding["StartLine"]?.GetValue<int>() ?? finding["startLine"]?.GetValue<int>() ?? 1;
        var endLine = finding["EndLine"]?.GetValue<int>() ?? finding["endLine"]?.GetValue<int>() ?? startLine;
        var startColumn = finding["StartColumn"]?.GetValue<int>() ?? finding["startColumn"]?.GetValue<int>() ?? 1;
        var endColumn = finding["EndColumn"]?.GetValue<int>() ?? finding["endColumn"]?.GetValue<int>() ?? startColumn;
        fingerprints.Add(ComputeFingerprint(path, ruleId, startLine, startColumn, endLine, endColumn));
    }

    private static string GetUnavailableReason(Exception exception) => exception switch
    {
        JsonException => "Gitleaks produced an invalid report.",
        SecurityScannerUnavailableException unavailable => unavailable.Message,
        _ => exception.Message,
    };

    private static void DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record SecurityScanOutput(IReadOnlyList<SecurityFindingRecord> Findings, string Version, int FilesScanned);
}
