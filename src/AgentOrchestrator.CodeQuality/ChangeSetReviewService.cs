using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed class ChangeSetReviewService
{
    public const string SchemaId = "https://quality.studio/schemas/change-review.v1.schema.json";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IChangeCoverageProvider? coverageProvider;

    public ChangeSetReviewService(IChangeCoverageProvider? coverageProvider = null) =>
        this.coverageProvider = coverageProvider;

    public async Task<IReadOnlyList<ChangeReviewResult>> ReviewAsync(
        IChangeSetProvider provider,
        ChangeSetQuery query,
        ChangeReviewOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new ChangeReviewOptions();
        var root = GitPlumbing.RequireRepository(query.RepositoryRoot);
        var changeSets = await provider.GetAsync(query with { RepositoryRoot = root }, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<ChangeReviewResult>(changeSets.Count);
        foreach (var changeSet in changeSets.Reverse())
        {
            results.Add(await ReviewAsync(root, changeSet, options, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<ChangeReviewResult> ReviewAsync(
        string repositoryRoot,
        ChangeSet changeSet,
        ChangeReviewOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        options ??= new ChangeReviewOptions();
        var root = GitPlumbing.RequireRepository(repositoryRoot);
        var before = await LoadMetadataAsync(root, changeSet.BaseCommit, cancellationToken).ConfigureAwait(false);
        var after = await LoadMetadataAsync(root, changeSet.ResultCommit, cancellationToken).ConfigureAwait(false);
        var onlyMoves = changeSet.TouchedFiles.Count > 0 &&
                        changeSet.TouchedFiles.All(path =>
                            path.Kind == ChangeKind.Renamed && !path.ContentChanged);
        var renameMap = changeSet.TouchedFiles
            .Where(path => path.PreviousPath is not null)
            .ToDictionary(path => path.PreviousPath!, path => path.Path, StringComparer.Ordinal);
        var touchedBefore = before.Where(meta => IsTouched(meta, changeSet, beforeSide: true)).ToArray();
        var touchedAfter = after.Where(meta => IsTouched(meta, changeSet, beforeSide: false)).ToArray();
        var pairs = PairMetadata(touchedBefore, touchedAfter, renameMap);
        var grades = BuildGradeDelta(pairs, onlyMoves);
        var findings = BuildFindingDelta(pairs, onlyMoves);
        var newlyStale = onlyMoves
            ? []
            : await BuildStalenessDeltaAsync(root, changeSet, pairs, renameMap, cancellationToken)
                .ConfigureAwait(false);
        var boundaries = await BuildBoundaryDeltaAsync(root, changeSet, renameMap, onlyMoves, cancellationToken)
            .ConfigureAwait(false);
        var repositoryFiles = await CountFilesAsync(root, changeSet.ResultCommit, cancellationToken).ConfigureAwait(false);
        var churn = BuildChurn(changeSet, repositoryFiles);
        var coverage = coverageProvider is null
            ? new CoverageDelta("unavailable", Reason: "Coverage ingestion is not available.")
            : await coverageProvider.GetDeltaAsync(changeSet, cancellationToken).ConfigureAwait(false);
        var hasQualityDelta = !onlyMoves &&
                              (grades.Any(item => item.ScoreChange != 0) ||
                               findings.New.Count > 0 || findings.Resolved.Count > 0 ||
                               newlyStale.Count > 0 ||
                               boundaries.New.Count > 0 || boundaries.Changed.Count > 0 ||
                               boundaries.Removed.Count > 0 ||
                               coverage.PercentagePointChange is not (null or 0));
        var deterministic = new DeterministicChangeDelta(
            grades, findings, newlyStale, boundaries, coverage, churn, onlyMoves, hasQualityDelta);
        var diffArguments = new List<string>
        {
            "diff", "--find-renames", "--unified=3", changeSet.BaseCommit, changeSet.ResultCommit, "--",
        };
        diffArguments.AddRange(ReviewablePaths(changeSet));
        var diff = diffArguments.Count == 6
            ? string.Empty
            : await GitPlumbing.RunAsync(root, diffArguments, cancellationToken).ConfigureAwait(false);
        var economy = await MeasureEconomyAsync(root, changeSet, diff, cancellationToken).ConfigureAwait(false);
        var judgement = options.Reviewer is null
            ? ChangeJudgement.NotRun
            : await options.Reviewer.ReviewAsync(root, changeSet, deterministic, diff, cancellationToken)
                .ConfigureAwait(false);
        ValidateJudgement(judgement);
        var verdict = DetermineVerdict(deterministic, grades);
        var summary = BuildSummary(deterministic, verdict);
        var document = new ChangeReviewDocument(
            SchemaId,
            SchemaVersion,
            new ChangeSetSubject(
                changeSet.Provider, changeSet.BaseCommit, changeSet.HeadCommit, changeSet.MergeCommit,
                changeSet.Title, changeSet.CommittedAt, changeSet.TouchedFiles),
            DateTimeOffset.UtcNow,
            BuildTouchedUnits(changeSet, touchedBefore, touchedAfter, renameMap),
            deterministic,
            judgement,
            economy,
            verdict,
            summary);
        var path = GetPath(root, changeSet);
        if (options.Persist) await SaveAsync(path, document, cancellationToken).ConfigureAwait(false);
        return new ChangeReviewResult(document, path);
    }

    public static string Serialize(ChangeReviewDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions) + "\n";

    public static string GetPath(string repositoryRoot, ChangeSet changeSet) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "changes",
            (changeSet.MergeCommit ?? changeSet.HeadCommit) + ".json");

    private static async Task SaveAsync(
        string path,
        ChangeReviewDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, Serialize(document), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<IReadOnlyList<MetaSnapshot>> LoadMetadataAsync(
        string root,
        string revision,
        CancellationToken cancellationToken)
    {
        var tree = await GitPlumbing.RunAsync(
            root, ["ls-tree", "-r", "--name-only", "-z", revision], cancellationToken).ConfigureAwait(false);
        var result = new List<MetaSnapshot>();
        foreach (var path in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .Where(path => path.Contains(".review-meta.", StringComparison.Ordinal) &&
                                    path.EndsWith(".json", StringComparison.Ordinal)))
        {
            var content = await ReadFileAsync(root, revision, path, cancellationToken).ConfigureAwait(false);
            try
            {
                var json = JsonNode.Parse(content!)?.AsObject();
                if (json?["unit"] is not JsonObject unit ||
                    json["grade"] is not JsonObject grade ||
                    json["kind"] is not JsonValue kindNode ||
                    !kindNode.TryGetValue<string>(out var kind)) continue;
                var unitId = unit["id"]?.GetValue<string>();
                var unitPath = unit["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(unitPath)) continue;
                var findings = (json["findings"] as JsonArray)?.OfType<JsonObject>()
                    .Select(finding => new FindingSnapshot(
                        Identity(finding),
                        finding["ruleId"]?.GetValue<string>() ?? "unknown",
                        finding["severity"]?.GetValue<string>() ?? "info",
                        finding["title"]?.GetValue<string>() ?? "Untitled finding"))
                    .ToArray() ?? [];
                var inputs = (json["subjectInputs"] as JsonArray)?.OfType<JsonObject>()
                    .Select(input => new MetaInput(
                        Normalize(input["path"]?.GetValue<string>() ?? string.Empty),
                        input["selector"]?.GetValue<string>() ?? string.Empty,
                        input["contentHash"]?.GetValue<string>() ?? string.Empty))
                    .ToArray() ?? [];
                result.Add(new MetaSnapshot(
                    path,
                    unitId!,
                    Normalize(unitPath!),
                    unit["level"]?.GetValue<string>() ?? "file",
                    unit["adapter"]?.GetValue<string>() ?? "unknown",
                    kind!,
                    grade["score"]!.GetValue<int>(),
                    grade["band"]!.GetValue<string>(),
                    findings,
                    inputs));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new ChangeReviewException($"Cannot parse review metadata '{path}' at '{revision}'.", exception);
            }
        }

        return result;
    }

    private static string Identity(JsonObject finding)
    {
        var value = finding["fingerprint"]?.GetValue<string>() ?? finding["id"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? "legacy:" + Hash(finding.ToJsonString()) : value;
    }

    private static bool IsTouched(MetaSnapshot meta, ChangeSet changeSet, bool beforeSide)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changeSet.TouchedFiles)
            paths.Add(beforeSide && path.PreviousPath is not null ? path.PreviousPath : path.Path);
        return paths.Contains(meta.MetaPath) || paths.Contains(meta.UnitPath) ||
               meta.Inputs.Any(input => paths.Contains(input.Path));
    }

    private static IReadOnlyList<MetaPair> PairMetadata(
        IReadOnlyList<MetaSnapshot> before,
        IReadOnlyList<MetaSnapshot> after,
        IReadOnlyDictionary<string, string> renameMap)
    {
        var afterById = after.ToDictionary(meta => meta.UnitId + "\0" + meta.Kind, StringComparer.Ordinal);
        var afterByPath = after.ToDictionary(meta => meta.UnitPath + "\0" + meta.Kind, StringComparer.Ordinal);
        var used = new HashSet<MetaSnapshot>();
        var result = new List<MetaPair>();
        foreach (var old in before)
        {
            afterById.TryGetValue(old.UnitId + "\0" + old.Kind, out var current);
            var translated = Translate(old.UnitPath, renameMap);
            current ??= afterByPath.GetValueOrDefault(translated + "\0" + old.Kind);
            if (current is not null) used.Add(current);
            result.Add(new MetaPair(old, current));
        }
        result.AddRange(after.Where(meta => !used.Contains(meta)).Select(meta => new MetaPair(null, meta)));
        return result;
    }

    private static IReadOnlyList<UnitGradeDelta> BuildGradeDelta(
        IReadOnlyList<MetaPair> pairs,
        bool onlyMoves) =>
        pairs.Select(pair =>
        {
            var unit = pair.After ?? pair.Before!;
            var before = pair.Before is null ? null : new GradeSnapshot(pair.Before.Score, pair.Before.Band);
            var after = pair.After is null ? null : new GradeSnapshot(pair.After.Score, pair.After.Band);
            int? change = before is null || after is null || onlyMoves ? null : after.Score - before.Score;
            return new UnitGradeDelta(
                unit.UnitId, unit.UnitPath, unit.Kind, before, after, change, change < 0);
        }).OrderBy(delta => delta.UnitPath, StringComparer.Ordinal).ThenBy(delta => delta.Kind, StringComparer.Ordinal).ToArray();

    private static FindingDelta BuildFindingDelta(IReadOnlyList<MetaPair> pairs, bool onlyMoves)
    {
        if (onlyMoves) return new FindingDelta([], [], []);
        var added = new List<FindingDeltaItem>();
        var resolved = new List<FindingDeltaItem>();
        var persisting = new List<FindingDeltaItem>();
        foreach (var pair in pairs)
        {
            var unit = pair.After ?? pair.Before!;
            var old = pair.Before?.Findings.ToDictionary(finding => finding.Identity, StringComparer.Ordinal) ?? [];
            var current = pair.After?.Findings.ToDictionary(finding => finding.Identity, StringComparer.Ordinal) ?? [];
            added.AddRange(current.Keys.Except(old.Keys, StringComparer.Ordinal)
                .Select(id => FindingItem(current[id], unit)));
            resolved.AddRange(old.Keys.Except(current.Keys, StringComparer.Ordinal)
                .Select(id => FindingItem(old[id], unit)));
            persisting.AddRange(current.Keys.Intersect(old.Keys, StringComparer.Ordinal)
                .Select(id => FindingItem(current[id], unit)));
        }
        return new FindingDelta(Sort(added), Sort(resolved), Sort(persisting));
    }

    private static FindingDeltaItem FindingItem(FindingSnapshot finding, MetaSnapshot unit) =>
        new(finding.Identity, unit.UnitId, unit.UnitPath, unit.Kind,
            finding.RuleId, finding.Severity, finding.Title);

    private static IReadOnlyList<FindingDeltaItem> Sort(IEnumerable<FindingDeltaItem> values) =>
        values.OrderBy(value => value.UnitPath, StringComparer.Ordinal)
            .ThenBy(value => value.Identity, StringComparer.Ordinal).ToArray();

    private static async Task<IReadOnlyList<UnitStalenessDelta>> BuildStalenessDeltaAsync(
        string root,
        ChangeSet changeSet,
        IReadOnlyList<MetaPair> pairs,
        IReadOnlyDictionary<string, string> renameMap,
        CancellationToken cancellationToken)
    {
        var result = new List<UnitStalenessDelta>();
        foreach (var pair in pairs.Where(pair => pair.After is not null))
        {
            var afterReasons = await StaleReasonsAsync(
                root, changeSet.ResultCommit, pair.After!, renameMap, cancellationToken).ConfigureAwait(false);
            if (afterReasons.Count == 0) continue;
            var beforeReasons = pair.Before is null
                ? []
                : await StaleReasonsAsync(
                    root, changeSet.BaseCommit, pair.Before, new Dictionary<string, string>(), cancellationToken)
                    .ConfigureAwait(false);
            if (pair.Before is null || beforeReasons.Count == 0)
                result.Add(new UnitStalenessDelta(
                    pair.After!.UnitId, pair.After.UnitPath, pair.After.Kind, afterReasons));
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> StaleReasonsAsync(
        string root,
        string revision,
        MetaSnapshot meta,
        IReadOnlyDictionary<string, string> renameMap,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        foreach (var input in meta.Inputs.Where(input => input.Selector is "file" or "aggregate-control"))
        {
            var path = Translate(input.Path, renameMap);
            var content = await ReadFileAsync(root, revision, path, cancellationToken, allowMissing: true)
                .ConfigureAwait(false);
            if (content is null)
            {
                reasons.Add($"reviewed input '{input.Path}' is missing");
                continue;
            }
            var actual = "sha256:" + Hash(NormalizeText(content));
            if (!StringComparer.Ordinal.Equals(actual, input.ContentHash))
                reasons.Add($"reviewed input '{input.Path}' changed");
        }
        return reasons;
    }

    private static async Task<BoundaryDelta> BuildBoundaryDeltaAsync(
        string root,
        ChangeSet changeSet,
        IReadOnlyDictionary<string, string> renameMap,
        bool onlyMoves,
        CancellationToken cancellationToken)
    {
        if (onlyMoves) return new BoundaryDelta([], [], []);
        var before = new Dictionary<string, BoundarySnapshot>(StringComparer.Ordinal);
        var after = new Dictionary<string, BoundarySnapshot>(StringComparer.Ordinal);
        foreach (var changed in changeSet.TouchedFiles)
        {
            var oldPath = changed.PreviousPath ?? changed.Path;
            var oldContent = await ReadFileAsync(root, changeSet.BaseCommit, oldPath, cancellationToken, true)
                .ConfigureAwait(false);
            var newContent = await ReadFileAsync(root, changeSet.ResultCommit, changed.Path, cancellationToken, true)
                .ConfigureAwait(false);
            if (oldContent is not null)
                foreach (var boundary in ChangeSetBoundaryScanner.Extract(oldPath, oldContent, renameMap))
                    before[boundary.Identity] = boundary;
            if (newContent is not null)
                foreach (var boundary in ChangeSetBoundaryScanner.Extract(changed.Path, newContent, null))
                    after[boundary.Identity] = boundary;
        }

        var addedSnapshots = after.Keys.Except(before.Keys, StringComparer.Ordinal)
            .Select(key => after[key]).ToList();
        var removedSnapshots = before.Keys.Except(after.Keys, StringComparer.Ordinal)
            .Select(key => before[key]).ToList();
        var changedBoundaries = after.Keys.Intersect(before.Keys, StringComparer.Ordinal)
            .Where(key => !StringComparer.Ordinal.Equals(after[key].Signature, before[key].Signature))
            .Select(key => after[key]).ToList();
        foreach (var removed in removedSnapshots.ToArray())
        {
            var replacement = addedSnapshots.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Path, removed.Path) &&
                StringComparer.Ordinal.Equals(candidate.Kind, removed.Kind) &&
                candidate.Line == removed.Line);
            if (replacement is null) continue;
            removedSnapshots.Remove(removed);
            addedSnapshots.Remove(replacement);
            changedBoundaries.Add(replacement);
        }
        return new BoundaryDelta(
            SortBoundaries(addedSnapshots.Select(ToChange)),
            SortBoundaries(changedBoundaries.Select(ToChange)),
            SortBoundaries(removedSnapshots.Select(ToChange)));
    }

    private static BoundaryChange ToChange(BoundarySnapshot value) =>
        new(value.Identity, value.Kind, value.Name, value.Path, value.Line, PathUnitId(value.Path));

    private static IReadOnlyList<BoundaryChange> SortBoundaries(IEnumerable<BoundaryChange> values) =>
        values.OrderBy(value => value.Path, StringComparer.Ordinal)
            .ThenBy(value => value.Line).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray();

    private static ChangeChurn BuildChurn(ChangeSet changeSet, int repositoryFiles)
    {
        var files = changeSet.TouchedFiles;
        var blast = repositoryFiles == 0 ? 0 : Math.Round(files.Count * 100d / repositoryFiles, 2);
        return new ChangeChurn(
            files.Count,
            files.Count(path => path.Kind == ChangeKind.Added),
            files.Count(path => path.Kind == ChangeKind.Modified),
            files.Count(path => path.Kind == ChangeKind.Deleted),
            files.Count(path => path.Kind is ChangeKind.Renamed or ChangeKind.Copied),
            files.Sum(path => path.Additions ?? 0),
            files.Sum(path => path.Deletions ?? 0),
            repositoryFiles,
            blast);
    }

    private static async Task<ChangeReviewEconomy> MeasureEconomyAsync(
        string root,
        ChangeSet changeSet,
        string diff,
        CancellationToken cancellationToken)
    {
        var files = 0;
        var characters = 0;
        var reviewable = ReviewablePaths(changeSet).ToHashSet(StringComparer.Ordinal);
        foreach (var path in changeSet.TouchedFiles.Where(path => reviewable.Contains(path.Path)))
        {
            var revision = path.Kind == ChangeKind.Deleted ? changeSet.BaseCommit : changeSet.ResultCommit;
            var contentPath = path.Kind == ChangeKind.Deleted ? path.PreviousPath ?? path.Path : path.Path;
            var content = await ReadFileAsync(root, revision, contentPath, cancellationToken, true)
                .ConfigureAwait(false);
            if (content is null || content.IndexOf('\0') >= 0) continue;
            files++;
            characters += content.Length;
        }
        var diffLines = diff.Count(character => character == '\n');
        var ratio = characters == 0 ? 0 : Math.Round(diff.Length * 100d / characters, 2);
        return new ChangeReviewEconomy(
            reviewable.Count, files, diffLines, diff.Length, characters,
            ratio, Math.Round(Math.Max(0, 100 - ratio), 2));
    }

    private static IEnumerable<string> ReviewablePaths(ChangeSet changeSet) =>
        changeSet.TouchedFiles
            .Where(path => !path.Binary)
            .Select(path => path.Path)
            .Where(path => !path.Contains(".review-meta.", StringComparison.Ordinal) &&
                           !path.StartsWith(".quality/", StringComparison.Ordinal) &&
                           !path.Contains("/.quality/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

    private static IReadOnlyList<TouchedHierarchyUnit> BuildTouchedUnits(
        ChangeSet changeSet,
        IReadOnlyList<MetaSnapshot> before,
        IReadOnlyList<MetaSnapshot> after,
        IReadOnlyDictionary<string, string> renameMap)
    {
        var units = new Dictionary<string, TouchedHierarchyUnit>(StringComparer.Ordinal)
        {
            ["path:project:."] = new("path:project:.", "path", "project", ".", "Repository"),
        };
        foreach (var path in changeSet.TouchedFiles)
        {
            var normalized = path.Path;
            var segments = normalized.Split('/');
            if (segments.Length > 1)
            {
                var modulePath = segments[0] == "src" && segments.Length > 2
                    ? string.Join('/', segments.Take(2))
                    : segments[0];
                var moduleId = "path:module:" + modulePath;
                units[moduleId] = new(moduleId, "path", "module", modulePath, modulePath);
            }
            var fileId = PathUnitId(normalized);
            units[fileId] = new(fileId, "path", "file", normalized, Path.GetFileName(normalized));
        }
        foreach (var meta in before.Concat(after))
        {
            var path = Translate(meta.UnitPath, renameMap);
            units[meta.UnitId] = new(meta.UnitId, meta.Adapter, meta.Level, path, Path.GetFileName(path));
        }
        return units.Values.OrderBy(unit => LevelOrder(unit.Level))
            .ThenBy(unit => unit.Path, StringComparer.Ordinal).ToArray();
    }

    private static ChangeReviewVerdict DetermineVerdict(
        DeterministicChangeDelta delta,
        IReadOnlyList<UnitGradeDelta> grades)
    {
        if (delta.OnlyMoves || !delta.HasQualityDelta) return ChangeReviewVerdict.NoQualityDelta;
        if (grades.Any(grade => grade.Regression) || delta.Findings.New.Count > 0 ||
            delta.NewlyStale.Count > 0 || delta.Boundaries.New.Count > 0 ||
            delta.Boundaries.Changed.Count > 0 || delta.Coverage.PercentagePointChange < 0)
            return ChangeReviewVerdict.Regression;
        if (grades.Any(grade => grade.ScoreChange > 0) || delta.Findings.Resolved.Count > 0 ||
            delta.Boundaries.Removed.Count > 0 || delta.Coverage.PercentagePointChange > 0)
            return ChangeReviewVerdict.Improved;
        return ChangeReviewVerdict.Neutral;
    }

    private static string BuildSummary(DeterministicChangeDelta delta, ChangeReviewVerdict verdict)
    {
        if (delta.OnlyMoves)
            return "No quality delta: the change set only moved files without changing their content.";
        if (!delta.HasQualityDelta)
            return "No quality delta was observed in the standing review evidence for the touched units.";
        var facts = new List<string>();
        var lower = delta.Grades.Where(grade => grade.Regression).ToArray();
        if (lower.Length > 0) facts.Add($"{lower.Length} touched unit grade(s) decreased");
        if (delta.Findings.New.Count > 0) facts.Add($"{delta.Findings.New.Count} finding(s) are new");
        if (delta.NewlyStale.Count > 0) facts.Add($"{delta.NewlyStale.Count} touched unit(s) became stale");
        if (delta.Boundaries.New.Count > 0) facts.Add($"{delta.Boundaries.New.Count} externally reachable boundary/boundaries are new");
        if (delta.Boundaries.Changed.Count > 0) facts.Add($"{delta.Boundaries.Changed.Count} externally reachable boundary/boundaries changed");
        if (delta.Findings.Resolved.Count > 0) facts.Add($"{delta.Findings.Resolved.Count} finding(s) were resolved");
        return $"{verdict}: {string.Join("; ", facts)}.";
    }

    private static void ValidateJudgement(ChangeJudgement judgement)
    {
        var required = new[] { "risk", "test-evidence", "scope-discipline", "architecture-drift" };
        if (required.Any(id => judgement.Aspects.All(aspect => !StringComparer.Ordinal.Equals(aspect.Id, id))))
            throw new ChangeReviewException("Change judgement must contain risk, test-evidence, scope-discipline, and architecture-drift.");
    }

    private static async Task<int> CountFilesAsync(
        string root,
        string revision,
        CancellationToken cancellationToken)
    {
        var files = await GitPlumbing.RunAsync(
            root, ["ls-tree", "-r", "--name-only", "-z", revision], cancellationToken).ConfigureAwait(false);
        return files.Count(character => character == '\0');
    }

    private static async Task<string?> ReadFileAsync(
        string root,
        string revision,
        string path,
        CancellationToken cancellationToken,
        bool allowMissing = false)
    {
        try
        {
            return await GitPlumbing.RunAsync(
                root, ["show", $"{revision}:{path}"], cancellationToken).ConfigureAwait(false);
        }
        catch (ChangeReviewException) when (allowMissing)
        {
            return null;
        }
    }

    private static string Translate(string path, IReadOnlyDictionary<string, string>? renameMap) =>
        renameMap is not null && renameMap.TryGetValue(path, out var translated) ? translated : path;

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    private static string NormalizeText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PathUnitId(string path) => "path:file:" + Hash(path);

    private static int LevelOrder(string level) => level switch
    {
        "project" => 0,
        "module" => 1,
        "namespace" => 2,
        "file" => 3,
        "function" => 4,
        _ => 5,
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private sealed record MetaSnapshot(
        string MetaPath,
        string UnitId,
        string UnitPath,
        string Level,
        string Adapter,
        string Kind,
        int Score,
        string Band,
        IReadOnlyList<FindingSnapshot> Findings,
        IReadOnlyList<MetaInput> Inputs);

    private sealed record FindingSnapshot(string Identity, string RuleId, string Severity, string Title);
    private sealed record MetaInput(string Path, string Selector, string ContentHash);
    private sealed record MetaPair(MetaSnapshot? Before, MetaSnapshot? After);
}

internal static class ChangeSetBoundaryScanner
{
    private static readonly Regex[] Patterns =
    [
        new(@"\bMap(?<method>Get|Post|Put|Delete|Patch|Methods)\s*\(\s*[""'](?<name>[^""']+)[""']",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\[(?<method>HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch)\s*(?:\(\s*[""'](?<name>[^""']*)[""']\s*\))?\]",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"@\w+\.(?<method>route|get|post|put|delete|patch)\s*\(\s*[""'](?<name>[^""']+)[""']",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\b(?:app|router)\.(?<method>get|post|put|delete|patch)\s*\(\s*[""'](?<name>[^""']+)[""']",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
    ];

    public static IReadOnlyList<BoundarySnapshot> Extract(
        string path,
        string content,
        IReadOnlyDictionary<string, string>? renameMap)
    {
        if (content.IndexOf('\0') >= 0 || content.Length > 2_000_000) return [];
        var canonicalPath = renameMap is not null && renameMap.TryGetValue(path, out var translated)
            ? translated
            : path;
        var result = new List<BoundarySnapshot>();
        foreach (var pattern in Patterns)
        {
            foreach (Match match in pattern.Matches(content))
            {
                var method = match.Groups["method"].Value.ToUpperInvariant().Replace("HTTP", string.Empty, StringComparison.Ordinal);
                var name = match.Groups["name"].Value;
                if (string.IsNullOrEmpty(name)) name = "(attribute route)";
                var line = content.AsSpan(0, match.Index).Count('\n') + 1;
                var display = $"{method} {name}";
                var identity = "boundary:" + Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{canonicalPath}\0{method}\0{name}")));
                var lineStart = content.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
                var lineEnd = content.IndexOf('\n', match.Index);
                if (lineEnd < 0) lineEnd = content.Length;
                result.Add(new BoundarySnapshot(
                    identity, "http-endpoint", display, canonicalPath, line,
                    Regex.Replace(content[lineStart..lineEnd], @"\s+", " ", RegexOptions.CultureInvariant).Trim()));
            }
        }
        return result.DistinctBy(boundary => boundary.Identity, StringComparer.Ordinal).ToArray();
    }
}

internal sealed record BoundarySnapshot(
    string Identity,
    string Kind,
    string Name,
    string Path,
    int Line,
    string Signature);
