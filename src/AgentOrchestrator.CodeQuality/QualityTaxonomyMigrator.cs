using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyMigrationItem(
    string SourcePath,
    string Domain,
    string Status,
    string? ObservationId = null,
    string? Message = null);

public sealed record QualityTaxonomyMigrationReport(
    int SchemaVersion,
    string Mode,
    string RepositoryRoot,
    DateTimeOffset GeneratedAt,
    int Imported,
    int Skipped,
    int AmbiguousSource,
    int UnknownModel,
    int Errors,
    IReadOnlyList<QualityTaxonomyMigrationItem> Items);

/// <summary>Non-destructive backfill from durable v1/v2 domain documents.</summary>
public static class QualityTaxonomyMigrator
{
    private static readonly JsonSerializerOptions FlowOptions = CreateDomainOptions(JsonNamingPolicy.CamelCase);
    private static readonly JsonSerializerOptions ChangeOptions = CreateDomainOptions(JsonNamingPolicy.KebabCaseLower);

    public static async Task<QualityTaxonomyMigrationReport> MigrateAsync(
        string repositoryRoot,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");

        var existing = (await QualityObservationLedger.ReadAsync(root, cancellationToken).ConfigureAwait(false))
            .Select(item => item.ObservationId)
            .ToHashSet(StringComparer.Ordinal);
        var usage = await UsageLedger.QueryAsync(
            root, recentLimit: 200, cancellationToken: cancellationToken).ConfigureAwait(false);
        var usageByRun = usage.Recent.GroupBy(item => item.RunId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Timestamp).First(),
                StringComparer.Ordinal);
        var candidates = new List<(string Source, string Domain, QualityObservationDocument Observation)>();
        var lifecycleCandidates = new List<IssueLifecycleEvent>();
        var items = new List<QualityTaxonomyMigrationItem>();
        var errors = 0;

        var statePath = Path.Combine(root,
            FindingStateStore.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(statePath))
        {
            try
            {
                var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath, cancellationToken)
                    .ConfigureAwait(false))?.AsObject()
                    ?? throw new JsonException("Finding state must be an object.");
                foreach (var record in (state["findings"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var fingerprint = RequiredText(record, "fingerprint");
                    var occurrence = Text(record["occurrenceFingerprint"]) ??
                                     FindingIdentity.OccurrenceFingerprint(fingerprint);
                    var issueId = Text(record["issueId"]) ?? FindingIdentity.IssueId(occurrence);
                    var legacyState = RequiredText(record, "state");
                    var lifecycle = QualityLegacyMapper.Map(
                        LegacyQualityVocabulary.FindingState, legacyState).Lifecycle!;
                    var occurredAt = DateTimeOffset.Parse(RequiredText(record, "timestamp")).ToUniversalTime();
                    var aliases = (record["fingerprintAliases"] as JsonArray)?.OfType<JsonValue>()
                        .Select(item => item.GetValue<string>()).Append(fingerprint)
                        .Distinct(StringComparer.Ordinal).ToArray() ?? [fingerprint];
                    var resolved = lifecycle == "resolved";
                    lifecycleCandidates.Add(IssueLifecycleStore.CreateEvent(
                        issueId,
                        occurrence,
                        aliases,
                        lifecycle,
                        "imported",
                        RequiredText(record, "author"),
                        RequiredText(record, "reason"),
                        occurredAt,
                        DateTimeOffset.TryParse(Text(record["expiresAt"]), out var expiry)
                            ? expiry.ToUniversalTime() : null,
                        resolved ? [$"legacy-state:{fingerprint}"] : null,
                        resolved ? "legacy-finding-state-snapshot@1" : null));
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or FormatException)
            {
                errors++;
                items.Add(new QualityTaxonomyMigrationItem(
                    FindingStateStore.RelativePath, "issue-lifecycle", "error", Message: exception.Message));
            }
        }

        foreach (var path in Enumerate(root, "*.review-meta.*.json"))
        {
            await AddAsync(path, "review-meta", async () =>
            {
                var metadata = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
                    ?.AsObject() ?? throw new JsonException("Review metadata must be an object.");
                var runId = metadata["reviewer"]?["runId"]?.GetValue<string>();
                usageByRun.TryGetValue(runId ?? string.Empty, out var exactUsage);
                return QualityDomainObservationAdapters.FromReviewMeta(
                    metadata, Relative(root, path), exactUsage);
            }).ConfigureAwait(false);
        }

        foreach (var path in Enumerate(root, "*.flow-review.json"))
        {
            await AddAsync(path, "deep-flow", async () =>
            {
                await using var stream = File.OpenRead(path);
                var report = await JsonSerializer.DeserializeAsync<FlowReviewReport>(
                    stream, FlowOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("Flow report must be an object.");
                return QualityDomainObservationAdapters.FromFlow(report, Relative(root, path));
            }).ConfigureAwait(false);
        }

        var attackPath = Path.Combine(root,
            AttackCoverageLedger.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(attackPath))
        {
            try
            {
                foreach (var observation in await new AttackCoverageLedger(root).ReadAsync(cancellationToken)
                             .ConfigureAwait(false))
                {
                    candidates.Add((AttackCoverageLedger.RelativePath, "attack-coverage",
                        QualityDomainObservationAdapters.FromAttack(
                            observation, AttackCoverageLedger.RelativePath)));
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                errors++;
                items.Add(new QualityTaxonomyMigrationItem(
                    AttackCoverageLedger.RelativePath, "attack-coverage", "error", Message: exception.Message));
            }
        }

        var changesDirectory = Path.Combine(root, ".quality", "changes");
        if (Directory.Exists(changesDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(changesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                await AddAsync(path, "change-review", async () =>
                {
                    await using var stream = File.OpenRead(path);
                    var document = await JsonSerializer.DeserializeAsync<ChangeReviewDocument>(
                        stream, ChangeOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new JsonException("Change review must be an object.");
                    return QualityDomainObservationAdapters.FromChange(document, Relative(root, path));
                }).ConfigureAwait(false);
            }
        }

        var imported = 0;
        var skipped = 0;
        var ambiguous = 0;
        var unknownModel = 0;
        var existingEvents = (await IssueLifecycleStore.ReadAsync(root, cancellationToken).ConfigureAwait(false))
            .Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
        foreach (var lifecycleEvent in lifecycleCandidates.OrderBy(item => item.EventId, StringComparer.Ordinal))
        {
            if (!existingEvents.Add(lifecycleEvent.EventId))
            {
                skipped++;
                items.Add(new QualityTaxonomyMigrationItem(
                    FindingStateStore.RelativePath, "issue-lifecycle", "skipped", lifecycleEvent.EventId,
                    "The deterministic lifecycle snapshot already exists."));
                continue;
            }
            if (apply)
            {
                var appended = await IssueLifecycleStore.AppendAsync(root, lifecycleEvent, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!appended)
                {
                    skipped++;
                    items.Add(new QualityTaxonomyMigrationItem(
                        FindingStateStore.RelativePath, "issue-lifecycle", "skipped", lifecycleEvent.EventId,
                        "The deterministic lifecycle snapshot already exists."));
                    continue;
                }
            }
            imported++;
            items.Add(new QualityTaxonomyMigrationItem(
                FindingStateStore.RelativePath, "issue-lifecycle", apply ? "imported" : "would-import",
                lifecycleEvent.EventId));
        }
        foreach (var candidate in candidates.OrderBy(item => item.Source, StringComparer.Ordinal)
                     .ThenBy(item => item.Observation.ObservationId, StringComparer.Ordinal))
        {
            if ((candidate.Observation.Producer.Kind is "unknown" or "imported" &&
                 candidate.Observation.Producer.Agent == "unknown") ||
                candidate.Observation.Findings.Any(item => item.Source.Kind == "unknown"))
                ambiguous++;
            if (candidate.Observation.Producer.EffectiveModel == "unknown") unknownModel++;
            if (!existing.Add(candidate.Observation.ObservationId))
            {
                skipped++;
                items.Add(new QualityTaxonomyMigrationItem(
                    candidate.Source, candidate.Domain, "skipped", candidate.Observation.ObservationId,
                    "The deterministic import already exists."));
                continue;
            }

            if (apply)
            {
                var appended = await QualityObservationLedger.AppendAsync(
                    root, candidate.Observation, CancellationToken.None).ConfigureAwait(false);
                if (!appended)
                {
                    skipped++;
                    items.Add(new QualityTaxonomyMigrationItem(
                        candidate.Source, candidate.Domain, "skipped", candidate.Observation.ObservationId,
                        "The deterministic import already exists."));
                    continue;
                }
            }
            imported++;
            items.Add(new QualityTaxonomyMigrationItem(
                candidate.Source, candidate.Domain, apply ? "imported" : "would-import",
                candidate.Observation.ObservationId));
        }

        return new QualityTaxonomyMigrationReport(
            1,
            apply ? "apply" : "dry-run",
            root,
            DateTimeOffset.UtcNow,
            imported,
            skipped,
            ambiguous,
            unknownModel,
            errors,
            items.OrderBy(item => item.SourcePath, StringComparer.Ordinal)
                .ThenBy(item => item.ObservationId, StringComparer.Ordinal).ToArray());

        async Task AddAsync(
            string path,
            string domain,
            Func<Task<QualityObservationDocument>> create)
        {
            try
            {
                candidates.Add((Relative(root, path), domain, await create().ConfigureAwait(false)));
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
            {
                errors++;
                items.Add(new QualityTaxonomyMigrationItem(
                    Relative(root, path), domain, "error", Message: exception.Message));
            }
        }
    }

    private static IEnumerable<string> Enumerate(string root, string pattern)
    {
        var quality = Path.Combine(root, ".quality");
        return Directory.Exists(quality)
            ? Directory.EnumerateFiles(quality, pattern, SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
            : [];
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string RequiredText(JsonObject value, string property) =>
        Text(value[property]) ?? throw new JsonException($"Finding state requires '{property}'.");

    private static string? Text(JsonNode? node) => node is JsonValue value &&
        value.TryGetValue<string>(out var text) ? text : null;

    private static JsonSerializerOptions CreateDomainOptions(JsonNamingPolicy enumPolicy)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(enumPolicy));
        return options;
    }
}
