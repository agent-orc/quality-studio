using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyMigrationItem(string Kind, string SourcePath, string Status, string? Message = null);

public sealed record QualityTaxonomyMigrationReport(
    int SchemaVersion,
    string Mode,
    int Imported,
    int Skipped,
    int AmbiguousSource,
    int UnknownModel,
    int Errors,
    IReadOnlyList<QualityTaxonomyMigrationItem> Items);

public sealed class QualityTaxonomyMigrator
{
    private static readonly JsonSerializerOptions CamelOptions = CreateOptions(JsonNamingPolicy.CamelCase);
    private static readonly JsonSerializerOptions KebabOptions = CreateOptions(JsonNamingPolicy.KebabCaseLower);

    public async Task<QualityTaxonomyMigrationReport> MigrateAsync(
        string repositoryRoot,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var store = new QualityObservationStore(root);
        var usageByRun = ReadUsage(root);
        var existing = (await store.ReadAllAsync(cancellationToken).ConfigureAwait(false)).Observations
            .Select(item => item.ObservationId).ToHashSet(StringComparer.Ordinal);
        var items = new List<QualityTaxonomyMigrationItem>();
        var imported = 0;
        var skipped = 0;
        var ambiguous = 0;
        var unknownModel = 0;
        var errors = 0;

        async Task ImportAsync(
            string kind,
            string path,
            Func<string, QualityObservation> create,
            string? suppliedPayload = null,
            string? displayedPath = null)
        {
            var relative = displayedPath ?? Relative(root, path);
            try
            {
                var payload = suppliedPayload ??
                              await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var observation = create(payload);
                if (usageByRun.TryGetValue(observation.Producer.RunId, out var usage))
                {
                    observation = observation with
                    {
                        Producer = observation.Producer with
                        {
                            RequestedModel = observation.Producer.RequestedModel == "unknown"
                                ? usage.Model : observation.Producer.RequestedModel,
                            EffectiveModel = observation.Producer.EffectiveModel == "unknown"
                                ? usage.Model : observation.Producer.EffectiveModel,
                            ReviewRunId = observation.Producer.ReviewRunId ?? usage.ReviewRunId,
                        },
                    };
                }
                if (existing.Contains(observation.ObservationId))
                {
                    skipped++;
                    items.Add(new(kind, relative, "skipped", "Deterministic import id already exists."));
                    return;
                }
                if (observation.Producer.Kind == QualityProducerKind.Unknown ||
                    observation.Findings.Any(finding => finding.Source.Kind == QualityProducerKind.Unknown))
                {
                    ambiguous++;
                    items.Add(new(kind, relative, apply ? "imported" : "would-import", "Producer is ambiguous."));
                }
                else
                {
                    items.Add(new(kind, relative, apply ? "imported" : "would-import"));
                }
                if (observation.Producer.EffectiveModel == "unknown") unknownModel++;
                if (apply)
                {
                    await store.AppendAsync(observation, cancellationToken).ConfigureAwait(false);
                    existing.Add(observation.ObservationId);
                }
                imported++;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or InvalidOperationException)
            {
                errors++;
                items.Add(new(kind, relative, "error", exception.Message));
            }
        }

        foreach (var path in Enumerate(root, "*.json").Where(IsSidecar))
        {
            await ImportAsync("review-meta", path, payload =>
            {
                var relative = Relative(root, path);
                var node = JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException("Review metadata must be an object.");
                return QualityDomainObservationAdapters.FromReviewMeta(node, relative,
                    QualityDomainObservationAdapters.ImportId("review-meta", relative, payload));
            }).ConfigureAwait(false);
        }
        foreach (var path in Enumerate(root, "*.flow-review.json"))
        {
            await ImportAsync("flow-review", path, payload =>
            {
                var relative = Relative(root, path);
                var report = JsonSerializer.Deserialize<FlowReviewReport>(payload, CamelOptions)
                    ?? throw new JsonException("Flow review must be an object.");
                return QualityDomainObservationAdapters.FromFlow(report, relative,
                    QualityDomainObservationAdapters.ImportId("flow-review", relative, payload));
            }).ConfigureAwait(false);
        }
        var changes = Path.Combine(root, ".quality", "changes");
        if (Directory.Exists(changes))
        {
            foreach (var path in Directory.EnumerateFiles(changes, "*.json", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                await ImportAsync("change-review", path, payload =>
                {
                    var relative = Relative(root, path);
                    var document = JsonSerializer.Deserialize<ChangeReviewDocument>(payload, KebabOptions)
                        ?? throw new JsonException("Change review must be an object.");
                    return QualityDomainObservationAdapters.FromChange(document, relative,
                        QualityDomainObservationAdapters.ImportId("change-review", relative, payload));
                }).ConfigureAwait(false);
            }
        }
        var attackPath = Path.Combine(root, AttackCoverageLedger.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(attackPath))
        {
            var index = 0;
            await foreach (var line in File.ReadLinesAsync(attackPath, cancellationToken).ConfigureAwait(false))
            {
                index++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var lineNumber = index;
                var relative = Relative(root, attackPath) + $"#line={lineNumber}";
                await ImportAsync("attack-coverage", attackPath, payload =>
                {
                    var attack = JsonSerializer.Deserialize<AttackCoverageObservation>(payload, KebabOptions)
                        ?? throw new JsonException("Attack coverage observation must be an object.");
                    return QualityDomainObservationAdapters.FromAttack(attack, relative,
                        QualityDomainObservationAdapters.ImportId("attack-coverage", relative, payload));
                }, line, relative).ConfigureAwait(false);
            }
        }

        var statePath = Path.Combine(root, FindingStateStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(statePath))
        {
            try
            {
                var states = ReadLegacyStateSnapshot(statePath);
                var lifecycleStore = new FindingLifecycleStore(root);
                var existingEvents = ReadEventIds(lifecycleStore.Path);
                foreach (var state in states.Values.OrderBy(item => item.Fingerprint, StringComparer.Ordinal))
                {
                    var eventId = "lifecycle-import:" + state.Fingerprint;
                    if (existingEvents.Contains(eventId))
                    {
                        skipped++;
                        items.Add(new("finding-state", Relative(root, statePath), "skipped", eventId));
                        continue;
                    }
                    var lifecycleState = QualityLegacyMappings.MapLifecycle(FindingStateStore.StateName(state.State));
                    var lifecycleEvent = new FindingLifecycleEvent(
                        2,
                        eventId,
                        QualityObservationIdentity.IssueId(state.Path, state.RuleId, null),
                        [state.Fingerprint],
                        state.FindingId,
                        state.Path,
                        state.RuleId,
                        lifecycleState,
                        state.Author,
                        state.Reason,
                        state.Timestamp,
                        state.ExpiresAt,
                        lifecycleState == QualityLifecycleState.Resolved ? ["legacy-state:" + state.Fingerprint] : null,
                        lifecycleState == QualityLifecycleState.Resolved ? "legacy-state-snapshot@1" : null);
                    if (apply) await lifecycleStore.AppendAsync(lifecycleEvent, cancellationToken).ConfigureAwait(false);
                    imported++;
                    items.Add(new("finding-state", Relative(root, statePath), apply ? "imported" : "would-import"));
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or InvalidOperationException)
            {
                errors++;
                items.Add(new("finding-state", Relative(root, statePath), "error", exception.Message));
            }
        }

        return new QualityTaxonomyMigrationReport(
            1, apply ? "apply" : "dry-run", imported, skipped, ambiguous, unknownModel, errors, items);
    }

    public static string SerializeReport(QualityTaxonomyMigrationReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        }) + Environment.NewLine;

    private static IEnumerable<string> Enumerate(string root, string pattern)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(root, pattern, options)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    private static bool IsSidecar(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.quality/reviews/", StringComparison.Ordinal) &&
               normalized.Contains(".review-meta.", StringComparison.Ordinal);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static HashSet<string> ReadEventIds(string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("eventId", out var id) && id.GetString() is { } value)
                    result.Add(value);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, FindingStateRecord> ReadLegacyStateSnapshot(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new JsonException("Finding state schemaVersion is unsupported.");
        var result = new Dictionary<string, FindingStateRecord>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("findings").EnumerateArray())
        {
            var fingerprint = item.GetProperty("fingerprint").GetString()!;
            var state = item.GetProperty("state").GetString() switch
            {
                "open" => FindingState.Open,
                "accepted" => FindingState.Accepted,
                "waived" => FindingState.Waived,
                "false-positive" => FindingState.FalsePositive,
                "resolved" => FindingState.Resolved,
                var value => throw new JsonException($"Unsupported finding state '{value}'."),
            };
            result[fingerprint] = new FindingStateRecord(
                fingerprint,
                item.GetProperty("findingId").GetString()!,
                item.GetProperty("path").GetString()!,
                item.GetProperty("ruleId").GetString()!,
                state,
                item.GetProperty("author").GetString()!,
                item.GetProperty("reason").GetString()!,
                item.GetProperty("timestamp").GetDateTimeOffset(),
                item.TryGetProperty("expiresAt", out var expiresAt) ? expiresAt.GetDateTimeOffset() : null);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, ReviewUsageEntry> ReadUsage(string root)
    {
        var directory = Path.Combine(root, ".quality", "usage");
        var result = new Dictionary<string, ReviewUsageEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var usage = JsonSerializer.Deserialize<ReviewUsageEntry>(line,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (usage is not null && !string.IsNullOrWhiteSpace(usage.RunId)) result[usage.RunId] = usage;
                }
                catch (JsonException) { }
            }
        }
        return result;
    }

    private static JsonSerializerOptions CreateOptions(JsonNamingPolicy policy)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(policy));
        return options;
    }
}
