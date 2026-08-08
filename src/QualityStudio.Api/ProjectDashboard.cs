using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ProjectDashboardResponse(
    string GeneratedAt,
    IReadOnlyList<ProjectGradeResponse> Grades,
    ProjectFindingsResponse Findings,
    ProjectStalenessResponse Staleness,
    ProjectReviewCoverageResponse ReviewCoverage,
    ProjectTestCoverageResponse TestCoverage,
    ProjectStructuralMetricsResponse Metrics,
    IReadOnlyList<ProjectHotspotResponse> Hotspots);

public sealed record ProjectGradeResponse(
    string Kind, string State, int? Score, string? Band, string Path);

public sealed record ProjectFindingsResponse(
    int Open,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByReviewState,
    string Path);

public sealed record ProjectStalenessResponse(
    int Fresh, int Stale, int Missing, int Total, string Path);

public sealed record ProjectReviewCoverageResponse(
    int ReviewedFiles, int TotalFiles, double Percent, string Path);

public sealed record ProjectTestCoverageResponse(
    string Status,
    double? LinePercent,
    int? CoveredLines,
    int? TotalLines,
    string? Source,
    string Path);

public sealed record ProjectStructuralMetricsResponse(
    int FileCount,
    int FolderCount,
    long Bytes,
    int Lines,
    IReadOnlyList<ProjectLanguageMetricResponse> Languages,
    IReadOnlyList<ProjectDistributionBucketResponse> FileSizeDistribution,
    IReadOnlyList<ProjectDistributionBucketResponse> FolderSizeDistribution,
    IReadOnlyList<ProjectDuplicationCandidateResponse> DuplicationCandidates,
    IReadOnlyList<ProjectDependencyEdgeResponse> DependencyEdges);

public sealed record ProjectLanguageMetricResponse(
    string Language, int Files, int Lines, long Bytes, string Path);

public sealed record ProjectDistributionBucketResponse(string Label, int Count);

public sealed record ProjectDuplicationCandidateResponse(
    string Fingerprint, int Lines, long Bytes, IReadOnlyList<string> Paths);

public sealed record ProjectDependencyEdgeResponse(
    string Source, string SourcePath, string Target, string TargetPath, string Kind);

public sealed record ProjectHotspotResponse(
    string Path,
    int Churn,
    int? Grade,
    int Findings,
    double FindingsPerKloc,
    double Risk);

public sealed record ProjectDashboardMeasurement(
    ProjectDashboardResponse Dashboard,
    bool CacheHit,
    double ProjectionMilliseconds);

/// <summary>
/// Builds the repository-level read model. Facts are calculated independently of review
/// grades, then composed only in the hotspot projection.
/// </summary>
public sealed class ProjectDashboardService
{
    private static readonly IReadOnlyDictionary<string, string> Languages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "C#", [".fs"] = "F#", [".vb"] = "Visual Basic",
            [".ts"] = "TypeScript", [".tsx"] = "TypeScript",
            [".js"] = "JavaScript", [".jsx"] = "JavaScript",
            [".py"] = "Python", [".java"] = "Java", [".kt"] = "Kotlin",
            [".go"] = "Go", [".rs"] = "Rust", [".cpp"] = "C++", [".cc"] = "C++",
            [".c"] = "C", [".h"] = "C/C++ header", [".hpp"] = "C/C++ header",
            [".rb"] = "Ruby", [".php"] = "PHP", [".swift"] = "Swift",
            [".html"] = "HTML", [".css"] = "CSS", [".scss"] = "SCSS",
            [".sql"] = "SQL", [".sh"] = "Shell", [".ps1"] = "PowerShell",
            [".json"] = "JSON", [".xml"] = "XML", [".yml"] = "YAML",
            [".yaml"] = "YAML", [".md"] = "Markdown",
        };

    private static readonly HashSet<string> TextExtensions =
        new(Languages.Keys, StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ProjectDashboardResponse> cache =
        new(StringComparer.Ordinal);

    public ProjectDashboardResponse Get(
        string repositoryPath,
        RepositoryHierarchySnapshot snapshot) =>
        GetMeasured(repositoryPath, snapshot).Dashboard;

    public ProjectDashboardMeasurement GetMeasured(
        string repositoryPath,
        RepositoryHierarchySnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        var root = Path.GetFullPath(repositoryPath);
        var key = root + "\0" + snapshot.GitState;
        if (cache.TryGetValue(key, out var cached))
        {
            return new ProjectDashboardMeasurement(
                cached,
                true,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var built = Build(root, snapshot.Roots);
        var dashboard = cache.GetOrAdd(key, built);
        return new ProjectDashboardMeasurement(
            dashboard,
            false,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public string ArchitectureReviewContext(
        string repositoryPath,
        RepositoryHierarchySnapshot snapshot)
    {
        var edges = Get(repositoryPath, snapshot).Metrics.DependencyEdges;
        var builder = new StringBuilder();
        builder.AppendLine("For this project-level code review, include a named aspect with id \"architecture\" and title \"Architecture\".");
        builder.AppendLine("Assess boundaries and coupling from these deterministic, declared module dependency edges; do not infer edges that are not listed:");
        if (edges.Count == 0)
        {
            builder.AppendLine("(no declared module dependency edges were discovered)");
        }
        else
        {
            foreach (var edge in edges)
                builder.AppendLine($"- {edge.Source} -> {edge.Target} ({edge.Kind})");
        }
        return builder.ToString().TrimEnd();
    }

    private static ProjectDashboardResponse Build(string root, IReadOnlyList<HierarchyNode> roots)
    {
        var hierarchy = Flatten(roots).ToArray();
        var hierarchyFiles = hierarchy
            .Where(node => node.Level == ReviewLevel.File)
            .DistinctBy(node => node.Path, StringComparer.Ordinal)
            .ToArray();
        var navigationPaths = hierarchy.Select(node => node.Path).ToHashSet(StringComparer.Ordinal);
        var repositoryFiles = EnumerateRepositoryFiles(root)
            .Select(path => ReadFileMetric(root, path))
            .ToArray();

        var projectPath = roots.FirstOrDefault()?.Path ?? ".";
        var grades = BuildGrades(roots, projectPath);
        var findings = BuildFindings(hierarchy, projectPath);
        var staleness = BuildStaleness(hierarchyFiles);
        var reviewCoverage = new ProjectReviewCoverageResponse(
            hierarchyFiles.Count(node => node.Documents.Count > 0),
            hierarchyFiles.Length,
            hierarchyFiles.Length == 0 ? 0 : Math.Round(
                hierarchyFiles.Count(node => node.Documents.Count > 0) * 100d / hierarchyFiles.Length, 1),
            hierarchyFiles.FirstOrDefault(node => node.Documents.Count == 0)?.Path ??
            hierarchyFiles.FirstOrDefault()?.Path ?? projectPath);
        var dependencies = BuildDependencyEdges(root, hierarchy);
        var metrics = BuildStructuralMetrics(repositoryFiles, dependencies, navigationPaths, projectPath);
        var churn = ReadGitChurn(root);
        var hotspots = BuildHotspots(hierarchyFiles, churn);

        return new ProjectDashboardResponse(
            DateTimeOffset.UtcNow.ToString("O"),
            grades,
            findings,
            staleness,
            reviewCoverage,
            ReadTestCoverage(root, repositoryFiles.Select(file => file.Path),
                hierarchyFiles.FirstOrDefault()?.Path ?? projectPath),
            metrics,
            hotspots);
    }

    private static IReadOnlyList<ProjectGradeResponse> BuildGrades(
        IReadOnlyList<HierarchyNode> roots, string fallbackPath)
    {
        var result = new List<ProjectGradeResponse>();
        foreach (var kind in Enum.GetValues<ReviewKind>())
        {
            var states = roots.Select(root =>
                root.AggregatedStates.TryGetValue(kind, out var state) ? state.Overall : ReviewState.NotReviewed).ToArray();
            var state = states.Any(candidate => candidate == ReviewState.Stale) ? "stale"
                : states.Any(candidate => candidate == ReviewState.Current) ? "fresh" : "missing";
            var direct = roots
                .Where(root => root.Documents.TryGetValue(kind, out _))
                .Select(root => (Root: root, Document: root.Documents[kind]))
                .ToArray();
            var scores = direct.Select(item => ReadGrade(item.Document.Payload))
                .Where(grade => grade.Score is not null).ToArray();
            var score = scores.Length == 0 ? null : (int?)Math.Round(scores.Average(grade => grade.Score!.Value));
            result.Add(new ProjectGradeResponse(
                kind.ToString().ToLowerInvariant(),
                state,
                score,
                score is null ? null : Band(score.Value),
                direct.FirstOrDefault().Root?.Path ?? roots.FirstOrDefault()?.Path ?? fallbackPath));
        }
        return result;
    }

    private static ProjectFindingsResponse BuildFindings(
        IReadOnlyList<HierarchyNode> hierarchy, string fallbackPath)
    {
        var severity = NewCountMap(["critical", "high", "medium", "low", "info"]);
        var reviewState = NewCountMap(["fresh", "stale"]);
        var open = 0;
        string? firstPath = null;
        foreach (var node in hierarchy)
        {
            foreach (var document in node.Documents.Values)
            {
                if (document.Payload is null) continue;
                using var json = JsonDocument.Parse(document.Payload);
                if (!json.RootElement.TryGetProperty("findings", out var findings) ||
                    findings.ValueKind != JsonValueKind.Array) continue;
                foreach (var finding in findings.EnumerateArray())
                {
                    // A review finding remains open until its persisted thread, when present,
                    // is resolved. Documents without a thread are actionable open findings.
                    if (FindingResolved(json.RootElement, finding)) continue;
                    open++;
                    var value = finding.TryGetProperty("severity", out var severityElement)
                        ? severityElement.GetString()?.ToLowerInvariant() : null;
                    if (value is not null && severity.ContainsKey(value)) severity[value]++;
                    var state = document.State == ReviewState.Stale ? "stale" : "fresh";
                    reviewState[state]++;
                    firstPath ??= FindingPath(finding) ?? node.Path;
                }
            }
        }
        return new ProjectFindingsResponse(open, severity, reviewState, firstPath ?? fallbackPath);
    }

    private static bool FindingResolved(JsonElement document, JsonElement finding)
    {
        if (!document.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array)
            return false;
        var fingerprint = finding.TryGetProperty("fingerprint", out var fingerprintElement)
            ? fingerprintElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        return threads.EnumerateArray().Any(thread =>
            thread.TryGetProperty("status", out var status) && status.GetString() == "resolved" &&
            thread.TryGetProperty("anchor", out var anchor) &&
            anchor.TryGetProperty("fingerprint", out var threadFingerprint) &&
            StringComparer.Ordinal.Equals(threadFingerprint.GetString(), fingerprint));
    }

    private static string? FindingPath(JsonElement finding)
    {
        if (!finding.TryGetProperty("locations", out var locations) ||
            locations.ValueKind != JsonValueKind.Array) return null;
        var first = locations.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object &&
               first.TryGetProperty("path", out var path) ? path.GetString() : null;
    }

    private static ProjectStalenessResponse BuildStaleness(IReadOnlyList<HierarchyNode> files)
    {
        var fresh = files.Count(node => NodeReviewState(node) == "fresh");
        var stale = files.Count(node => NodeReviewState(node) == "stale");
        var missing = files.Count - fresh - stale;
        var path = files.FirstOrDefault(node => NodeReviewState(node) == "stale")?.Path ??
                   files.FirstOrDefault(node => NodeReviewState(node) == "missing")?.Path ??
                   files.FirstOrDefault()?.Path ?? ".";
        return new ProjectStalenessResponse(fresh, stale, missing, files.Count, path);
    }

    private static string NodeReviewState(HierarchyNode node) =>
        node.Documents.Values.Any(document => document.State == ReviewState.Stale) ? "stale"
        : node.Documents.Count > 0 ? "fresh" : "missing";

    private static ProjectStructuralMetricsResponse BuildStructuralMetrics(
        IReadOnlyList<FileMetric> files,
        IReadOnlyList<ProjectDependencyEdgeResponse> dependencies,
        IReadOnlySet<string> navigationPaths,
        string fallbackPath)
    {
        var folders = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var directory = RepositoryDirectory(file.Path);
            while (directory != ".")
            {
                folders[directory] = folders.GetValueOrDefault(directory) + file.Bytes;
                directory = RepositoryDirectory(directory);
            }
        }
        var languages = files.Where(file => file.Language is not null)
            .GroupBy(file => file.Language!, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.Select(file => file.Path).FirstOrDefault(navigationPaths.Contains) ?? fallbackPath;
                return new ProjectLanguageMetricResponse(
                    group.Key, group.Count(), group.Sum(file => file.Lines),
                    group.Sum(file => file.Bytes), first);
            })
            .OrderByDescending(language => language.Lines)
            .ThenBy(language => language.Language, StringComparer.Ordinal)
            .ToArray();
        var duplicates = files
            .Where(file => file.DuplicateFingerprint is not null)
            .GroupBy(file => file.DuplicateFingerprint!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var ordered = group.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
                return new ProjectDuplicationCandidateResponse(
                    "sha256:" + group.Key, ordered[0].Lines, ordered[0].Bytes,
                    ordered.Select(file => file.Path).ToArray());
            })
            .OrderByDescending(candidate => candidate.Bytes * candidate.Paths.Count)
            .ThenBy(candidate => candidate.Fingerprint, StringComparer.Ordinal)
            .Take(50)
            .ToArray();
        return new ProjectStructuralMetricsResponse(
            files.Count,
            folders.Count,
            files.Sum(file => file.Bytes),
            files.Sum(file => file.Lines),
            languages,
            Distribution(files.Select(file => file.Bytes)),
            Distribution(folders.Values),
            duplicates,
            dependencies);
    }

    private static IReadOnlyList<ProjectDistributionBucketResponse> Distribution(IEnumerable<long> values)
    {
        var counts = new int[5];
        foreach (var value in values)
        {
            var index = value < 1_024 ? 0 : value < 10_240 ? 1 : value < 102_400 ? 2 : value < 1_048_576 ? 3 : 4;
            counts[index]++;
        }
        var labels = new[] { "< 1 KB", "1–10 KB", "10–100 KB", "100 KB–1 MB", "≥ 1 MB" };
        return labels.Select((label, index) => new ProjectDistributionBucketResponse(label, counts[index])).ToArray();
    }

    private static IReadOnlyList<ProjectDependencyEdgeResponse> BuildDependencyEdges(
        string root, IReadOnlyList<HierarchyNode> hierarchy)
    {
        var modules = hierarchy.Where(node => node.Level == ReviewLevel.Module).ToArray();
        var byProjectFile = modules
            .Where(module => module.Path.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .GroupBy(module => module.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edges = new List<ProjectDependencyEdgeResponse>();
        foreach (var source in byProjectFile.Values.OrderBy(module => module.Path, StringComparer.Ordinal))
        {
            var absolute = Path.Combine(root, Native(source.Path));
            if (!File.Exists(absolute)) continue;
            XDocument project;
            try { project = XDocument.Load(absolute); }
            catch (Exception exception) when (exception is IOException or System.Xml.XmlException) { continue; }
            foreach (var reference in project.Descendants()
                         .Where(element => element.Name.LocalName == "ProjectReference")
                         .Select(element => element.Attribute("Include")?.Value)
                         .Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var targetAbsolute = Path.GetFullPath(Native(reference!), Path.GetDirectoryName(absolute)!);
                if (!IsContained(root, targetAbsolute)) continue;
                var targetPath = Relative(root, targetAbsolute);
                if (!byProjectFile.TryGetValue(targetPath, out var target)) continue;
                edges.Add(new ProjectDependencyEdgeResponse(
                    source.Name, source.Path, target.Name, target.Path, "project-reference"));
            }
        }
        return edges.Distinct().OrderBy(edge => edge.SourcePath, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetPath, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ProjectHotspotResponse> BuildHotspots(
        IReadOnlyList<HierarchyNode> files, IReadOnlyDictionary<string, int> churn)
    {
        var result = new List<ProjectHotspotResponse>();
        foreach (var file in files)
        {
            var findings = 0;
            int? grade = null;
            foreach (var document in file.Documents.Values)
            {
                if (document.Payload is null) continue;
                using var json = JsonDocument.Parse(document.Payload);
                if (json.RootElement.TryGetProperty("findings", out var findingArray) &&
                    findingArray.ValueKind == JsonValueKind.Array)
                    findings += findingArray.EnumerateArray().Count(finding => !FindingResolved(json.RootElement, finding));
                if (document.Kind == ReviewKind.Code)
                    grade = ReadGrade(document.Payload).Score;
            }
            var lines = Math.Max(1, file.LineCount ?? 0);
            var density = Math.Round(findings * 1000d / lines, 2);
            var changes = churn.GetValueOrDefault(file.Path);
            var risk = Math.Round(Math.Log10(changes + 1) * (density + 1) * (101 - (grade ?? 50)) / 100d, 2);
            result.Add(new ProjectHotspotResponse(file.Path, changes, grade, findings, density, risk));
        }
        return result.OrderByDescending(hotspot => hotspot.Risk)
            .ThenByDescending(hotspot => hotspot.Churn)
            .ThenBy(hotspot => hotspot.Path, StringComparer.Ordinal)
            .Take(30).ToArray();
    }

    private static IReadOnlyDictionary<string, int> ReadGitChurn(string root)
    {
        var output = RunGit(root, "log", "-n", "200", "--numstat", "--format=");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (output is null) return result;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 3 || !int.TryParse(fields[0], out var added) ||
                !int.TryParse(fields[1], out var deleted)) continue;
            var path = fields[^1].Replace('\\', '/');
            result[path] = result.GetValueOrDefault(path) + added + deleted;
        }
        return result;
    }

    private static ProjectTestCoverageResponse ReadTestCoverage(
        string root, IEnumerable<string> repositoryFiles, string fallbackPath)
    {
        var reports = repositoryFiles.Where(path =>
                Path.GetFileName(path).Equals("coverage.cobertura.xml", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("coverage.xml", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("lcov.info", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var report in reports)
        {
            var absolute = Path.Combine(root, Native(report));
            try
            {
                if (report.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                {
                    var total = 0;
                    var covered = 0;
                    foreach (var line in File.ReadLines(absolute))
                    {
                        if (line.StartsWith("LF:", StringComparison.Ordinal)) total += int.Parse(line[3..], CultureInfo.InvariantCulture);
                        else if (line.StartsWith("LH:", StringComparison.Ordinal)) covered += int.Parse(line[3..], CultureInfo.InvariantCulture);
                    }
                    if (total > 0) return Coverage(covered, total, report, fallbackPath);
                }
                else
                {
                    var coverage = XDocument.Load(absolute).Root;
                    if (coverage is null) continue;
                    var covered = ParseInt(coverage.Attribute("lines-covered")?.Value);
                    var total = ParseInt(coverage.Attribute("lines-valid")?.Value);
                    if (covered is not null && total > 0) return Coverage(covered.Value, total.Value, report, fallbackPath);
                    var rate = ParseDouble(coverage.Attribute("line-rate")?.Value);
                    if (rate is not null)
                        return new ProjectTestCoverageResponse("reported", Math.Round(rate.Value * 100, 1), null, null, report, fallbackPath);
                }
            }
            catch (Exception exception) when (exception is IOException or FormatException or System.Xml.XmlException)
            {
                // A malformed report is not silently converted into a clean coverage result.
                return new ProjectTestCoverageResponse("invalid", null, null, null, report, fallbackPath);
            }
        }
        return new ProjectTestCoverageResponse("unavailable", null, null, null, null, fallbackPath);
    }

    private static ProjectTestCoverageResponse Coverage(int covered, int total, string source, string path) =>
        new("reported", Math.Round(covered * 100d / total, 1), covered, total, source, path);

    private static FileMetric ReadFileMetric(string root, string path)
    {
        var absolute = Path.Combine(root, Native(path));
        var info = new FileInfo(absolute);
        var language = Languages.GetValueOrDefault(Path.GetExtension(path));
        var lines = 0;
        string? duplicateFingerprint = null;
        if (TextExtensions.Contains(Path.GetExtension(path)) && info.Length <= 4 * 1024 * 1024)
        {
            try
            {
                var text = File.ReadAllText(absolute);
                lines = text.Length == 0 ? 0
                    : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);
                if ((lines >= 3 || info.Length >= 64) && info.Length <= 1024 * 1024)
                {
                    var normalized = string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Split('\n').Select(line => line.TrimEnd()));
                    duplicateFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
                }
            }
            catch (IOException) { }
        }
        return new FileMetric(path, info.Length, lines, language, duplicateFingerprint);
    }

    private static IReadOnlyList<string> EnumerateRepositoryFiles(string root)
    {
        var git = RunGit(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z");
        if (git is not null)
            return git.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('\\', '/'))
                .Where(path => !Excluded(path) && File.Exists(Path.Combine(root, Native(path))))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Relative(root, path))
            .Where(path => !Excluded(path))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static bool Excluded(string path) => path.Split('/').Any(part =>
        part is ".git" or ".quality" or "bin" or "obj" or "node_modules" or "dist" or "coverage");

    private static (int? Score, string? Band) ReadGrade(string? payload)
    {
        if (payload is null) return (null, null);
        using var json = JsonDocument.Parse(payload);
        if (!json.RootElement.TryGetProperty("grade", out var grade)) return (null, null);
        return (
            grade.TryGetProperty("score", out var score) && score.TryGetInt32(out var value) ? value : null,
            grade.TryGetProperty("band", out var band) ? band.GetString() : null);
    }

    private static Dictionary<string, int> NewCountMap(IEnumerable<string> keys) =>
        keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);

    private static string Band(int score) => score switch
    {
        >= 90 => "A", >= 80 => "B", >= 70 => "C", >= 60 => "D", _ => "F",
    };

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }

    private static string? RunGit(string root, params string[] arguments)
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
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string RepositoryDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "." : path[..index];
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Native(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static bool IsContained(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private sealed record FileMetric(
        string Path, long Bytes, int Lines, string? Language, string? DuplicateFingerprint);
}
