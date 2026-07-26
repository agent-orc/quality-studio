using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentOrchestrator.CodeQuality;

public sealed record CoverageFile(
    string Path,
    int CoveredLines,
    int TotalLines,
    int CoveredBranches,
    int TotalBranches,
    IReadOnlyList<int> UncoveredLines,
    IReadOnlyList<int> UncoveredBranchLines);

public sealed record CoverageSnapshot(
    int SchemaVersion,
    string SensorVersion,
    string MeasuredAt,
    string? Commit,
    IReadOnlyList<string> Reports,
    IReadOnlyList<CoverageFile> Files)
{
    public const string RelativePath = ".quality/coverage/coverage.json";

    public static CoverageSnapshot Empty(string measuredAt, string? commit, IReadOnlyList<string>? reports = null) =>
        new(1, CoverageSensor.CurrentVersion, measuredAt, commit, reports ?? [], []);

    public static CoverageSnapshot? Load(string repositoryRoot)
    {
        var path = System.IO.Path.Combine(repositoryRoot, RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CoverageSnapshot>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(repositoryRoot, RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

public sealed record CoverageAggregate(
    string State,
    int CoveredLines,
    int TotalLines,
    int CoveredBranches,
    int TotalBranches,
    decimal? LinePercent,
    decimal? BranchPercent,
    string? Commit,
    string? MeasuredAt,
    int FilesWithData,
    IReadOnlyList<int>? UncoveredLines = null,
    IReadOnlyList<int>? UncoveredBranchLines = null)
{
    public static CoverageAggregate Unknown { get; } =
        new("unknown", 0, 0, 0, 0, null, null, null, null, 0);
}

public static class CoverageProjection
{
    public static CoverageAggregate ForPath(
        CoverageSnapshot? snapshot,
        string? currentCommit,
        string path,
        bool file,
        IEnumerable<string>? descendantFiles = null)
    {
        if (snapshot is null) return CoverageAggregate.Unknown;
        var normalized = Normalize(path);
        var matches = file
            ? snapshot.Files.Where(item => StringComparer.Ordinal.Equals(Normalize(item.Path), normalized)).ToArray()
            : snapshot.Files.Where(item => (descendantFiles ?? []).Select(Normalize)
                .Contains(Normalize(item.Path), StringComparer.Ordinal)).ToArray();
        if (matches.Length == 0) return CoverageAggregate.Unknown;
        var coveredLines = matches.Sum(item => item.CoveredLines);
        var totalLines = matches.Sum(item => item.TotalLines);
        var coveredBranches = matches.Sum(item => item.CoveredBranches);
        var totalBranches = matches.Sum(item => item.TotalBranches);
        var state = !string.IsNullOrWhiteSpace(snapshot.Commit) &&
                    !string.IsNullOrWhiteSpace(currentCommit) &&
                    !StringComparer.Ordinal.Equals(snapshot.Commit, currentCommit)
            ? "stale"
            : "current";
        var single = file ? matches[0] : null;
        return new CoverageAggregate(
            state,
            coveredLines,
            totalLines,
            coveredBranches,
            totalBranches,
            Percent(coveredLines, totalLines),
            Percent(coveredBranches, totalBranches),
            snapshot.Commit,
            snapshot.MeasuredAt,
            matches.Length,
            single?.UncoveredLines,
            single?.UncoveredBranchLines);
    }

    public static string Evidence(CoverageSnapshot? snapshot, string? currentCommit, IEnumerable<string> paths)
    {
        if (snapshot is null || snapshot.Files.Count == 0)
            return "No coverage data is available. Treat coverage as unknown; do not infer 0% coverage.";
        var requested = paths.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var files = snapshot.Files.Where(file => requested.Contains(Normalize(file.Path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            return "No coverage data is available for the reviewed file(s). Treat coverage as unknown; do not infer 0% coverage.";
        var stale = !string.IsNullOrWhiteSpace(snapshot.Commit) && !string.IsNullOrWhiteSpace(currentCommit) &&
                    !StringComparer.Ordinal.Equals(snapshot.Commit, currentCommit);
        var builder = new StringBuilder();
        builder.Append("Coverage was measured at commit ").Append(snapshot.Commit ?? "unknown")
            .Append(" on ").Append(snapshot.MeasuredAt).Append(stale ? " and is stale for the current commit." : ".");
        foreach (var file in files)
        {
            builder.Append("\n- ").Append(file.Path).Append(": lines ")
                .Append(file.CoveredLines).Append('/').Append(file.TotalLines)
                .Append(" (").Append(FormatPercent(file.CoveredLines, file.TotalLines)).Append(')');
            if (file.TotalBranches > 0)
                builder.Append("; branches ").Append(file.CoveredBranches).Append('/').Append(file.TotalBranches)
                    .Append(" (").Append(FormatPercent(file.CoveredBranches, file.TotalBranches)).Append(')');
            if (file.UncoveredLines.Count > 0)
                builder.Append("; uncovered lines ").Append(CompactLines(file.UncoveredLines));
            if (file.UncoveredBranchLines.Count > 0)
                builder.Append("; uncovered branches on lines ").Append(CompactLines(file.UncoveredBranchLines));
            builder.Append('.');
        }
        builder.Append("\nUse this only as evidence: identify concrete untested behavior at those lines, and do not claim that coverage proves correctness.");
        return builder.ToString();
    }

    private static decimal? Percent(int covered, int total) =>
        total == 0 ? null : Math.Round(covered * 100m / total, 2, MidpointRounding.AwayFromZero);

    private static string FormatPercent(int covered, int total) =>
        total == 0 ? "unknown" : $"{Percent(covered, total):0.##}%";

    private static string CompactLines(IReadOnlyList<int> values)
    {
        var ordered = values.Distinct().Order().ToArray();
        var ranges = new List<string>();
        for (var index = 0; index < ordered.Length;)
        {
            var start = ordered[index];
            var end = start;
            while (++index < ordered.Length && ordered[index] == end + 1) end = ordered[index];
            ranges.Add(start == end ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{end}");
        }
        return string.Join(", ", ranges);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}

public sealed class CoverageReportParser
{
    public IReadOnlyList<CoverageFile> Parse(string repositoryRoot, IEnumerable<string> reportPaths)
    {
        var root = System.IO.Path.GetFullPath(repositoryRoot);
        var accumulator = new Dictionary<string, MutableCoverageFile>(StringComparer.Ordinal);
        foreach (var reportPath in reportPaths)
        {
            var fullPath = System.IO.Path.GetFullPath(reportPath);
            EnsureContainedReport(root, fullPath);
            var extension = System.IO.Path.GetExtension(fullPath);
            if (extension.Equals(".trx", StringComparison.OrdinalIgnoreCase))
            {
                ParseTrx(root, fullPath, accumulator);
            }
            else if (extension.Equals(".info", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".lcov", StringComparison.OrdinalIgnoreCase))
            {
                ParseLcov(root, fullPath, accumulator);
            }
            else
            {
                ParseXmlCoverage(root, fullPath, accumulator);
            }
        }
        return accumulator.Values.Select(value => value.Build()).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static void ParseTrx(string root, string path, Dictionary<string, MutableCoverageFile> files)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        if (HasCoveragePayload(document))
        {
            ParseXmlDocument(root, document, files);
        }
        foreach (var attachment in document.Descendants()
                     .Select(element => element.Attribute("href")?.Value ?? element.Attribute("path")?.Value)
                     .Where(value => !string.IsNullOrWhiteSpace(value) &&
                                     (value.EndsWith(".coverage", StringComparison.OrdinalIgnoreCase) ||
                                      value.EndsWith(".coveragexml", StringComparison.OrdinalIgnoreCase) ||
                                      value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))))
        {
            var decoded = Uri.UnescapeDataString(attachment!);
            var candidate = Uri.TryCreate(decoded, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : System.IO.Path.GetFullPath(decoded, System.IO.Path.GetDirectoryName(path)!);
            candidate = System.IO.Path.GetFullPath(candidate);
            EnsureContainedReport(root, candidate);
            if (File.Exists(candidate)) ParseXmlCoverage(root, candidate, files);
        }
    }

    private static bool HasCoveragePayload(XDocument document) =>
        document.Descendants().Any(element => element.Name.LocalName is
            "CoverageSession" or "CoverageDSPriv" or "coverage");

    private static void ParseXmlCoverage(string root, string path, Dictionary<string, MutableCoverageFile> files)
    {
        try
        {
            ParseXmlDocument(root, XDocument.Load(path, LoadOptions.None), files);
        }
        catch (XmlException exception)
        {
            var converted = ConvertNativeCoverage(path);
            if (converted is null)
                throw new InvalidDataException(
                    $"'{path}' is a native binary .coverage report and dotnet-coverage is not available to convert it without running tests.",
                    exception);
            try
            {
                ParseXmlDocument(root, XDocument.Load(converted, LoadOptions.None), files);
            }
            finally
            {
                File.Delete(converted);
            }
        }
    }

    private static string? ConvertNativeCoverage(string path)
    {
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quality-studio-{Guid.NewGuid():N}.cobertura.xml");
        var converted = false;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet-coverage")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in new[] { "merge", path, "-f", "cobertura", "-o", output })
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return null;
            process.WaitForExit();
            converted = process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
            return converted ? output : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            if (!converted && File.Exists(output)) File.Delete(output);
        }
    }

    private static void ParseXmlDocument(string root, XDocument document, Dictionary<string, MutableCoverageFile> files)
    {
        var rootName = document.Root?.Name.LocalName;
        if (rootName == "coverage")
        {
            ParseCobertura(root, document, files);
            return;
        }
        if (rootName == "CoverageSession")
        {
            ParseOpenCover(root, document, files);
            return;
        }
        ParseVisualStudio(root, document, files);
    }

    private static void ParseCobertura(string root, XDocument document, Dictionary<string, MutableCoverageFile> files)
    {
        foreach (var @class in document.Descendants().Where(element => element.Name.LocalName == "class"))
        {
            var source = @class.Attribute("filename")?.Value;
            if (string.IsNullOrWhiteSpace(source)) continue;
            var target = Get(files, ResolvePath(root, source));
            foreach (var line in @class.Descendants().Where(element => element.Name.LocalName == "line"))
            {
                if (!TryInt(line.Attribute("number")?.Value, out var number)) continue;
                var hits = TryInt(line.Attribute("hits")?.Value, out var parsedHits) ? parsedHits : 0;
                target.Line(number, hits);
                var conditionCoverage = line.Attribute("condition-coverage")?.Value;
                if (!string.IsNullOrWhiteSpace(conditionCoverage))
                {
                    var match = Regex.Match(conditionCoverage, @"\((\d+)\s*/\s*(\d+)\)");
                    if (match.Success)
                        target.Branches(number, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                }
            }
        }
    }

    private static void ParseLcov(string root, string path, Dictionary<string, MutableCoverageFile> files)
    {
        MutableCoverageFile? target = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                target = Get(files, ResolvePath(root, line[3..]));
            }
            else if (target is not null && line.StartsWith("DA:", StringComparison.Ordinal))
            {
                var values = line[3..].Split(',');
                if (values.Length >= 2 && TryInt(values[0], out var number) && TryInt(values[1], out var hits))
                    target.Line(number, hits);
            }
            else if (target is not null && line.StartsWith("BRDA:", StringComparison.Ordinal))
            {
                var values = line[5..].Split(',');
                if (values.Length >= 4 && TryInt(values[0], out var number))
                    target.Branch(number, values[3] != "-" && TryInt(values[3], out var taken) && taken > 0);
            }
            else if (line == "end_of_record")
            {
                target = null;
            }
        }
    }

    private static void ParseOpenCover(string root, XDocument document, Dictionary<string, MutableCoverageFile> files)
    {
        var sourceFiles = document.Descendants().Where(element => element.Name.LocalName == "File")
            .Where(element => element.Attribute("uid") is not null && element.Attribute("fullPath") is not null)
            .ToDictionary(element => element.Attribute("uid")!.Value, element => element.Attribute("fullPath")!.Value);
        foreach (var point in document.Descendants().Where(element => element.Name.LocalName == "SequencePoint"))
        {
            var fileId = point.Attribute("fileid")?.Value;
            if (fileId is null || !sourceFiles.TryGetValue(fileId, out var source) ||
                !TryInt(point.Attribute("sl")?.Value, out var number)) continue;
            Get(files, ResolvePath(root, source)).Line(number,
                TryInt(point.Attribute("vc")?.Value, out var visits) ? visits : 0);
        }
        foreach (var point in document.Descendants().Where(element => element.Name.LocalName == "BranchPoint"))
        {
            var fileId = point.Attribute("fileid")?.Value;
            if (fileId is null || !sourceFiles.TryGetValue(fileId, out var source) ||
                !TryInt(point.Attribute("sl")?.Value, out var number)) continue;
            Get(files, ResolvePath(root, source)).Branch(number,
                TryInt(point.Attribute("vc")?.Value, out var visits) && visits > 0);
        }
    }

    private static void ParseVisualStudio(string root, XDocument document, Dictionary<string, MutableCoverageFile> files)
    {
        var sources = document.Descendants().Where(element => element.Name.LocalName == "SourceFileName")
            .Where(element => element.Attribute("uid") is not null)
            .ToDictionary(element => element.Attribute("uid")!.Value, element => element.Value);
        foreach (var range in document.Descendants().Where(element => element.Name.LocalName == "Range"))
        {
            var sourceId = range.Attribute("source_id")?.Value ?? range.Attribute("sourceId")?.Value;
            if (sourceId is null || !sources.TryGetValue(sourceId, out var source) ||
                !TryInt(range.Attribute("start_line")?.Value ?? range.Attribute("startLine")?.Value, out var number)) continue;
            var covered = (range.Attribute("covered")?.Value ?? string.Empty) is "yes" or "true" or "1";
            Get(files, ResolvePath(root, source)).Line(number, covered ? 1 : 0);
        }
        foreach (var sourceFile in document.Descendants().Where(element => element.Name.LocalName == "source_file"))
        {
            var source = sourceFile.Attribute("path")?.Value ?? sourceFile.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(source)) continue;
            var target = Get(files, ResolvePath(root, source));
            foreach (var line in sourceFile.Descendants().Where(element => element.Name.LocalName == "line"))
            {
                if (!TryInt(line.Attribute("number")?.Value ?? line.Attribute("line")?.Value, out var number)) continue;
                var visits = TryInt(line.Attribute("visits")?.Value ?? line.Attribute("hits")?.Value, out var value) ? value : 0;
                target.Line(number, visits);
            }
            foreach (var branch in sourceFile.Descendants().Where(element => element.Name.LocalName == "branch"))
            {
                if (!TryInt(branch.Attribute("line")?.Value ?? branch.Attribute("number")?.Value, out var number)) continue;
                var visits = TryInt(branch.Attribute("visits")?.Value ?? branch.Attribute("hits")?.Value, out var value) ? value : 0;
                target.Branch(number, visits > 0);
            }
        }
    }

    private static MutableCoverageFile Get(Dictionary<string, MutableCoverageFile> files, string path)
    {
        if (!files.TryGetValue(path, out var value)) files[path] = value = new MutableCoverageFile(path);
        return value;
    }

    private static string ResolvePath(string root, string source)
    {
        var decoded = Uri.UnescapeDataString(source.Trim()).Replace('\\', '/');
        if (decoded.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            decoded = new Uri(decoded).LocalPath.Replace('\\', '/');
        var normalizedRoot = System.IO.Path.GetFullPath(root);
        if (System.IO.Path.IsPathRooted(decoded))
        {
            var full = System.IO.Path.GetFullPath(decoded);
            if (full.StartsWith(normalizedRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return System.IO.Path.GetRelativePath(normalizedRoot, full).Replace('\\', '/');
        }
        var relative = decoded.TrimStart('/');
        if (File.Exists(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar))))
            return relative;
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 1; index < segments.Length; index++)
        {
            var suffix = string.Join('/', segments[index..]);
            if (File.Exists(System.IO.Path.Combine(root, suffix.Replace('/', System.IO.Path.DirectorySeparatorChar))))
                return suffix;
        }
        return relative;
    }

    private static bool TryInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static void EnsureContainedReport(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = System.IO.Path.GetFullPath(root)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var normalizedPath = System.IO.Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot + System.IO.Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Coverage report paths must stay within the repository.");
        var current = normalizedRoot;
        foreach (var segment in System.IO.Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
                     [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Coverage report paths cannot traverse symbolic links or junctions.");
        }
    }

    private sealed class MutableCoverageFile(string path)
    {
        private readonly Dictionary<int, int> lines = [];
        private readonly List<(int Line, bool Covered)> branches = [];
        public void Line(int number, int hits) => lines[number] = Math.Max(hits, lines.GetValueOrDefault(number));
        public void Branch(int line, bool covered) => branches.Add((line, covered));
        public void Branches(int line, int covered, int total)
        {
            for (var index = 0; index < total; index++) branches.Add((line, index < covered));
        }
        public CoverageFile Build() => new(
            path,
            lines.Count(pair => pair.Value > 0),
            lines.Count,
            branches.Count(branch => branch.Covered),
            branches.Count,
            lines.Where(pair => pair.Value == 0).Select(pair => pair.Key).Order().ToArray(),
            branches.Where(branch => !branch.Covered).Select(branch => branch.Line).Distinct().Order().ToArray());
    }
}

public sealed class CoverageSensor : IReviewSensor
{
    public const string CurrentVersion = "1.0.0";
    public string Id => "coverage";
    public string Version => CurrentVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            ["parser"] = CurrentVersion,
        }));

    public async Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Scope != SensorScope.Repository)
            throw new ArgumentException("Coverage ingestion supports repository scope only.", nameof(request));
        var root = System.IO.Path.GetFullPath(request.RepositoryRoot);
        var reports = ResolveReports(root, request.Configuration).ToArray();
        var files = reports.Length == 0 ? [] : new CoverageReportParser().Parse(root, reports);
        var measuredAt = DateTimeOffset.UtcNow.ToString("O");
        var commit = GitValue(root, "rev-parse", "--verify", "HEAD");
        var relativeReports = reports.Select(report => System.IO.Path.GetRelativePath(root, report).Replace('\\', '/')).ToArray();
        var snapshot = new CoverageSnapshot(1, Version, measuredAt, commit, relativeReports, files);
        if (request.PersistMetadata) await snapshot.SaveAsync(root, cancellationToken).ConfigureAwait(false);
        return new SensorScanResult(true, reports.Length == 0 ? "No coverage reports matched the configured report paths." : null,
            [], new SensorProvenance(Id, Version, "repository", ".", measuredAt,
                new Dictionary<string, string> { ["parser"] = Version, ["reports"] = reports.Length.ToString(CultureInfo.InvariantCulture) }));
    }

    private static IEnumerable<string> ResolveReports(string root, IReadOnlyDictionary<string, string>? configuration)
    {
        var configured = configuration?.GetValueOrDefault("reportPaths");
        var patterns = string.IsNullOrWhiteSpace(configured)
            ? new[] { "coverage.cobertura.xml", "coverage.xml", "lcov.info", "**/*.trx", "**/*.coverage", "**/*.coveragexml" }
            : configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            if (System.IO.Path.IsPathRooted(pattern))
                throw new ArgumentException("Coverage report paths must be repository-relative.");
            if (pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
                throw new ArgumentException("Coverage report paths cannot escape the repository.");
            var normalized = pattern.Replace('\\', '/');
            if (!normalized.Contains('*') && !normalized.Contains('?'))
            {
                var exact = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (File.Exists(exact)) results.Add(exact);
                continue;
            }
            var regex = Glob(normalized);
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
                if (regex.IsMatch(relative)) results.Add(System.IO.Path.GetFullPath(file));
            }
        }
        return results.Order(StringComparer.Ordinal);
    }

    private static Regex Glob(string pattern)
    {
        var expression = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    expression.Append("(?:.*/)?");
                }
                else expression.Append(".*");
            }
            else if (pattern[index] == '*') expression.Append("[^/]*");
            else if (pattern[index] == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(pattern[index].ToString()));
        }
        expression.Append('$');
        return new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    public static string? GitValue(string root, params string[] arguments)
    {
        try
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
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public sealed class GitChurnAnalyzer
{
    public IReadOnlyDictionary<string, int> Analyze(string repositoryRoot, int days, DateTimeOffset? now = null)
    {
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days));
        var since = (now ?? DateTimeOffset.UtcNow).AddDays(-days).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var output = CoverageSensor.GitValue(repositoryRoot, "log", $"--since={since}", "--format=%x1e%H",
            "--name-only", "--no-renames", "--");
        if (string.IsNullOrWhiteSpace(output)) return new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var paths = record.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Select(path => path.Trim().Replace('\\', '/')).Where(path => path.Length > 0)
                .Distinct(StringComparer.Ordinal);
            foreach (var path in paths) result[path] = result.GetValueOrDefault(path) + 1;
        }
        return result;
    }
}
