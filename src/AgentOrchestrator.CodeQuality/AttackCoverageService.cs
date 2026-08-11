using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record BoundaryCoverageSnapshot(
    string BoundaryDefinitionHash,
    string CoveredCodeHash,
    IReadOnlyList<AttackEvidence> CoveredCodeEvidence);

public static partial class BoundaryCoverageHasher
{
    public static async Task<BoundaryCoverageSnapshot> SnapshotAsync(
        string repositoryRoot,
        BoundaryEntry boundary,
        CancellationToken cancellationToken = default)
    {
        var definition = new
        {
            boundary.Id,
            boundary.Kind,
            boundary.Direction,
            boundary.Name,
            boundary.Transport,
            LocationPath = boundary.Location.Path,
            boundary.Reachability,
            boundary.Authentication,
            boundary.Authorization,
            boundary.Inputs,
            boundary.Response,
            boundary.SideEffects,
            boundary.RateLimit,
            boundary.SizeLimit,
            boundary.KnownConsumers,
            boundary.Evidence,
        };
        var definitionHash = AttackCoverageJson.Hash(definition);
        var path = ResolveWithinRoot(repositoryRoot, boundary.Location.Path);
        if (!File.Exists(path))
        {
            return new BoundaryCoverageSnapshot(
                definitionHash,
                AttackCoverageJson.Hash(new { boundary.Location.Path, missing = true }),
                [new AttackEvidence("covered-code", boundary.Location.Path, "Boundary source file is missing.")]);
        }

        var content = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var fragments = new List<(string Selector, string Content)>
        {
            ($"line:{boundary.Location.Line}", StatementAtLine(content, boundary.Location.Line)),
        };
        foreach (var handler in boundary.Evidence.Select(HandlerName).Where(value => value is not null)
                     .Cast<string>().Distinct(StringComparer.Ordinal))
        {
            var method = MethodBody(content, handler);
            if (method.Length > 0) fragments.Add(($"symbol:{handler}", method));
        }
        var canonical = string.Join("\0", fragments
            .OrderBy(fragment => fragment.Selector, StringComparer.Ordinal)
            .Select(fragment => fragment.Selector + "\0" + fragment.Content.Trim()));
        var codeHash = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("quality-studio/boundary-code/v1\0" + canonical)));
        return new BoundaryCoverageSnapshot(
            definitionHash,
            codeHash,
            fragments.Select(fragment => new AttackEvidence(
                "covered-code",
                $"{boundary.Location.Path}#{fragment.Selector}",
                $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fragment.Content)))}"))
                .ToArray());
    }

    private static string StatementAtLine(string content, int oneBasedLine)
    {
        var lines = content.Split('\n');
        var index = Math.Clamp(oneBasedLine - 1, 0, Math.Max(0, lines.Length - 1));
        var builder = new StringBuilder();
        var balance = 0;
        for (var current = index; current < lines.Length && current < index + 200; current++)
        {
            var line = lines[current];
            builder.AppendLine(line);
            foreach (var character in line)
            {
                if (character is '(' or '{' or '[') balance++;
                else if (character is ')' or '}' or ']') balance--;
            }
            if (balance <= 0 && (line.Contains(';') || current > index && line.TrimEnd().EndsWith('}'))) break;
        }
        return builder.ToString().Trim();
    }

    private static string? HandlerName(string evidence)
    {
        const string prefix = "handler ";
        return evidence.StartsWith(prefix, StringComparison.Ordinal) && evidence.Length > prefix.Length
            ? evidence[prefix.Length..].Trim()
            : null;
    }

    private static string MethodBody(string content, string handler)
    {
        var match = Regex.Match(content,
            $@"(?m)^[^\r\n]*\b{Regex.Escape(handler)}\s*\([^;]*?(?:=>|\{{)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return string.Empty;
        var arrow = match.Value.LastIndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            var semicolon = content.IndexOf(';', match.Index + arrow);
            return semicolon < 0 ? match.Value : content[match.Index..(semicolon + 1)];
        }
        var open = content.IndexOf('{', match.Index);
        if (open < 0) return match.Value;
        var depth = 0;
        for (var index = open; index < content.Length; index++)
        {
            if (content[index] == '{') depth++;
            else if (content[index] == '}' && --depth == 0) return content[match.Index..(index + 1)];
        }
        return content[match.Index..];
    }

    private static string ResolveWithinRoot(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Boundary source escapes the repository.", nameof(relativePath));
        return path;
    }
}

/// <summary>Append-only repository ledger. Existing observations are never rewritten.</summary>
public sealed class AttackCoverageLedger
{
    public const string RelativePath = ".quality/attacks/coverage-ledger.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string repositoryRoot;
    private readonly string path;

    public AttackCoverageLedger(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.repositoryRoot = System.IO.Path.GetFullPath(repositoryRoot);
        path = System.IO.Path.Combine(
            this.repositoryRoot,
            RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public string Path => path;

    public async Task<IReadOnlyList<AttackCoverageObservation>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        var observations = new List<AttackCoverageObservation>();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var observation = JsonSerializer.Deserialize<AttackCoverageObservation>(
                    line, AttackCoverageJson.Options)
                    ?? throw new JsonException("Ledger line is null.");
                Validate(observation);
                observations.Add(observation);
            }
            catch (JsonException exception)
            {
                throw new JsonException($"Attack coverage ledger line {lineNumber} is invalid.", exception);
            }
        }
        return observations;
    }

    public async Task AppendAsync(
        AttackCoverageObservation observation,
        CancellationToken cancellationToken = default)
    {
        Validate(observation);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(observation, AttackCoverageJson.Options)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        await QualityObservationLedger.AppendAsync(
            repositoryRoot,
            QualityDomainObservationAdapters.FromAttack(observation, RelativePath),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static void Validate(AttackCoverageObservation observation)
    {
        if (observation.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(observation.AssessmentId) ||
            string.IsNullOrWhiteSpace(observation.BoundaryId) ||
            string.IsNullOrWhiteSpace(observation.AttackId) ||
            string.IsNullOrWhiteSpace(observation.Reasoning) ||
            string.IsNullOrWhiteSpace(observation.Reviewer.Agent) ||
            string.IsNullOrWhiteSpace(observation.Reviewer.Model) ||
            string.IsNullOrWhiteSpace(observation.Reviewer.ThinkingLevel) ||
            string.IsNullOrWhiteSpace(observation.PromptVersion) ||
            string.IsNullOrWhiteSpace(observation.PromptHash) ||
            string.IsNullOrWhiteSpace(observation.CatalogueVersion) ||
            !IsHash(observation.CatalogueEntryHash) ||
            !IsHash(observation.BoundaryDefinitionHash) ||
            !IsHash(observation.CoveredCodeHash) ||
            observation.TokenCost.InputTokens < 0 || observation.TokenCost.OutputTokens < 0 ||
            observation.TokenCost.CachedInputTokens < 0 || observation.TokenCost.ReasoningOutputTokens < 0)
            throw new JsonException("Attack coverage observation is incomplete.");
        if (observation.Verdict == AttackCoverageVerdict.NotYetChecked)
            throw new JsonException("Not-yet-checked is projected from missing/incomplete judgements, not appended as a verdict.");
        if (observation.Verdict == AttackCoverageVerdict.Finding &&
            string.IsNullOrWhiteSpace(observation.FindingId) &&
            string.IsNullOrWhiteSpace(observation.FindingFingerprint))
            throw new JsonException("A finding verdict must link to the finding lifecycle.");
        if (observation.Verdict != AttackCoverageVerdict.Finding &&
            (!string.IsNullOrWhiteSpace(observation.FindingId) ||
             !string.IsNullOrWhiteSpace(observation.FindingFingerprint)))
            throw new JsonException("Only a finding verdict may carry a finding link.");
        if (observation.Evidence.Count == 0)
            throw new JsonException("Every judgement requires evidence.");
    }

    private static bool IsHash(string value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class AttackCoverageService
{
    private const string DeterministicPromptVersion = "boundary-analyzer-rules.v1";
    private static readonly string DeterministicPromptHash = AttackCoverageJson.Hash(DeterministicPromptVersion);
    private readonly Func<DateTimeOffset> clock;

    public AttackCoverageService(Func<DateTimeOffset>? clock = null) =>
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<AttackCoverageMatrix> BuildAsync(
        string repositoryRoot,
        BoundaryInventory inventory,
        ResolvedAttackCatalogue catalogue,
        string scope = ".",
        bool recheckDeterministic = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(catalogue);
        var prompt = AttackCoveragePrompt.Reference();
        var ledger = new AttackCoverageLedger(repositoryRoot);
        var snapshots = new Dictionary<string, BoundaryCoverageSnapshot>(StringComparer.Ordinal);
        foreach (var boundary in inventory.Entries)
            snapshots[boundary.Id] = await BoundaryCoverageHasher.SnapshotAsync(
                repositoryRoot, boundary, cancellationToken).ConfigureAwait(false);

        var observations = (await ledger.ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var appended = await RefreshDeterministicAsync(
            repositoryRoot, inventory, catalogue, snapshots, observations, ledger,
            recheckDeterministic, cancellationToken).ConfigureAwait(false);
        observations.AddRange(appended);

        var now = clock().ToUniversalTime();
        var attacksById = catalogue.Entries.ToDictionary(item => item.Entry.Id, StringComparer.Ordinal);
        var rows = new List<AttackCoverageRow>();
        foreach (var boundary in inventory.Entries)
        {
            var snapshot = snapshots[boundary.Id];
            var cells = new List<AttackCoverageCell>();
            foreach (var resolved in catalogue.Entries.Where(item =>
                         AttackCatalogueResolver.Applies(item.Entry, boundary)))
            {
                var history = observations.Where(observation =>
                        observation.BoundaryId == boundary.Id && observation.AttackId == resolved.Entry.Id)
                    .GroupBy(observation => observation.AssessmentId, StringComparer.Ordinal)
                    .Select(group => ProjectHistory(group.OrderBy(item => item.CheckedAt).ToArray()))
                    .OrderBy(item => item.CheckedAt).ThenBy(item => item.AssessmentId, StringComparer.Ordinal)
                    .ToArray();
                cells.Add(ProjectCell(boundary.Id, resolved, snapshot, prompt, history, now));
            }
            var relevant = observations.Where(observation => observation.BoundaryId == boundary.Id).ToArray();
            var codeChanges = Math.Max(0, relevant.Select(item => item.CoveredCodeHash).Distinct(StringComparer.Ordinal).Count() - 1);
            var oldest = cells.Where(cell => cell.CheckedAt is not null).Select(cell => cell.CheckedAt!.Value)
                .DefaultIfEmpty().Min();
            rows.Add(new AttackCoverageRow(
                boundary, snapshot.BoundaryDefinitionHash, snapshot.CoveredCodeHash, codeChanges,
                oldest == default ? null : oldest, cells));
        }

        var orderedRows = rows
            .OrderByDescending(row => row.CodeChangeCount)
            .ThenBy(row => row.OldestVerdictAt ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.Boundary.Name, StringComparer.Ordinal)
            .ToArray();
        return new AttackCoverageMatrix(
            1, catalogue.Version, prompt.Version, prompt.ContentHash, now, scope,
            attacksById.Values.Select(item => item.Entry).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            orderedRows);
    }

    public async Task<AttackCoverageObservation> RecordAsync(
        string repositoryRoot,
        BoundaryInventory inventory,
        ResolvedAttackCatalogue catalogue,
        AttackJudgementSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (string.IsNullOrWhiteSpace(submission.Reasoning))
            throw new ArgumentException("Judgement reasoning is required.", nameof(submission));
        if (submission.Evidence is not { Count: > 0 })
            throw new ArgumentException("Judgement evidence is required.", nameof(submission));
        if (submission.Reviewer is null ||
            string.IsNullOrWhiteSpace(submission.Reviewer.Agent) ||
            string.IsNullOrWhiteSpace(submission.Reviewer.Model) ||
            string.IsNullOrWhiteSpace(submission.Reviewer.ThinkingLevel))
            throw new ArgumentException("Reviewer agent, model, and thinking level are required.", nameof(submission));
        if (submission.TokenCost is null)
            throw new ArgumentException("Token cost is required.", nameof(submission));
        if (submission.Source == AttackCoverageSource.DeterministicSensor)
            throw new ArgumentException("Deterministic observations are produced by the boundary sensor.", nameof(submission));
        var boundary = inventory.Entries.SingleOrDefault(item => item.Id == submission.BoundaryId)
            ?? throw new KeyNotFoundException($"Boundary '{submission.BoundaryId}' was not found.");
        var attack = catalogue.Entries.SingleOrDefault(item => item.Entry.Id == submission.AttackId)
            ?? throw new KeyNotFoundException($"Attack '{submission.AttackId}' was not found.");
        if (!AttackCatalogueResolver.Applies(attack.Entry, boundary))
            throw new ArgumentException("The attack does not apply to the selected boundary.", nameof(submission));
        var assessmentId = string.IsNullOrWhiteSpace(submission.AssessmentId)
            ? Guid.NewGuid().ToString("N")
            : submission.AssessmentId.Trim();
        var snapshot = await BoundaryCoverageHasher.SnapshotAsync(repositoryRoot, boundary, cancellationToken)
            .ConfigureAwait(false);
        var prompt = AttackCoveragePrompt.Reference();
        var observation = new AttackCoverageObservation(
            1,
            assessmentId,
            boundary.Id,
            attack.Entry.Id,
            submission.Verdict,
            submission.Reasoning.Trim(),
            submission.Evidence,
            submission.DeterministicSensorInput ?? [],
            submission.FindingId,
            submission.FindingFingerprint,
            submission.Source,
            submission.Reviewer,
            prompt.Version,
            prompt.ContentHash,
            catalogue.Version,
            attack.EntryHash,
            snapshot.BoundaryDefinitionHash,
            snapshot.CoveredCodeHash,
            submission.TokenCost,
            clock().ToUniversalTime(),
            submission.Commit ?? await GitAsync(repositoryRoot, "rev-parse", "HEAD").ConfigureAwait(false),
            submission.CommitRange);
        await new AttackCoverageLedger(repositoryRoot).AppendAsync(observation, cancellationToken).ConfigureAwait(false);
        var commonObservationId = QualityDomainObservationAdapters.FromAttack(
            observation, AttackCoverageLedger.RelativePath).ObservationId;
        await EnsureFindingLifecycleLinkAsync(
            repositoryRoot, boundary, attack.Entry, submission, commonObservationId, cancellationToken)
            .ConfigureAwait(false);
        return observation;
    }

    private async Task<IReadOnlyList<AttackCoverageObservation>> RefreshDeterministicAsync(
        string repositoryRoot,
        BoundaryInventory inventory,
        ResolvedAttackCatalogue catalogue,
        IReadOnlyDictionary<string, BoundaryCoverageSnapshot> snapshots,
        IReadOnlyList<AttackCoverageObservation> existing,
        AttackCoverageLedger ledger,
        bool recheckChanged,
        CancellationToken cancellationToken)
    {
        var appended = new List<AttackCoverageObservation>();
        var commit = await GitAsync(repositoryRoot, "rev-parse", "HEAD").ConfigureAwait(false);
        foreach (var boundary in inventory.Entries)
        {
            var snapshot = snapshots[boundary.Id];
            foreach (var attack in catalogue.Entries.Where(item =>
                         item.Entry.DeterministicRuleIds.Count > 0 &&
                         AttackCatalogueResolver.Applies(item.Entry, boundary)))
            {
                var matchingFindings = inventory.Findings.Where(finding =>
                    attack.Entry.DeterministicRuleIds.Contains(finding.RuleId, StringComparer.Ordinal) &&
                    FindingTargetsBoundary(finding, boundary)).ToArray();
                var hasPriorObservation = existing.Concat(appended).Any(observation =>
                    observation.Source == AttackCoverageSource.DeterministicSensor &&
                    observation.BoundaryId == boundary.Id &&
                    observation.AttackId == attack.Entry.Id);
                if (hasPriorObservation && !recheckChanged) continue;
                var previousFindings = existing.Concat(appended).Where(observation =>
                        observation.Source == AttackCoverageSource.DeterministicSensor &&
                        observation.BoundaryId == boundary.Id &&
                        observation.AttackId == attack.Entry.Id &&
                        observation.FindingFingerprint is not null)
                    .Select(observation => new FindingIdentityRecord(
                        observation.FindingFingerprint!,
                        observation.FindingId ?? "finding-" + observation.FindingFingerprint![7..],
                        boundary.Location.Path,
                        attack.Entry.Id))
                    .DistinctBy(item => item.Fingerprint, StringComparer.Ordinal)
                    .ToArray();
                var currentFindings = matchingFindings.Select(finding => new FindingIdentityRecord(
                        finding.Fingerprint, finding.Id, finding.Locations[0].Path, finding.RuleId))
                    .ToArray();
                if (currentFindings.Length > 0 || previousFindings.Length > 0)
                {
                    await new FindingStateStore(repositoryRoot).MergeReviewAsync(
                        currentFindings, previousFindings, "boundary-analyzer", cancellationToken)
                        .ConfigureAwait(false);
                }
                if (matchingFindings.Length == 0 && !attack.Entry.DeterministicPassConclusive)
                    continue;
                var inputs = attack.Entry.DeterministicRuleIds.Order(StringComparer.Ordinal)
                    .Select(rule => matchingFindings.Any(finding => finding.RuleId == rule)
                        ? $"{rule}:finding"
                        : $"{rule}:clear")
                    .ToArray();
                var alreadyCurrent = existing.Concat(appended).Any(observation =>
                    observation.Source == AttackCoverageSource.DeterministicSensor &&
                    observation.BoundaryId == boundary.Id &&
                    observation.AttackId == attack.Entry.Id &&
                    observation.BoundaryDefinitionHash == snapshot.BoundaryDefinitionHash &&
                    observation.CoveredCodeHash == snapshot.CoveredCodeHash &&
                    observation.CatalogueEntryHash == attack.EntryHash &&
                    observation.PromptHash == DeterministicPromptHash &&
                    observation.DeterministicSensorInput.SequenceEqual(inputs));
                if (alreadyCurrent) continue;

                var verdict = matchingFindings.Length > 0
                    ? AttackCoverageVerdict.Finding
                    : AttackCoverageVerdict.Pass;
                var evidence = matchingFindings.Length > 0
                    ? matchingFindings.Select(finding => new AttackEvidence(
                        "deterministic-finding", finding.Fingerprint,
                        $"{finding.RuleId}: {finding.Title}")).ToArray()
                    : snapshot.CoveredCodeEvidence.Concat([
                        new AttackEvidence("deterministic-sensor", $"boundaries@{inventory.SensorVersion}",
                            $"No applicable finding from: {string.Join(", ", attack.Entry.DeterministicRuleIds)}")
                    ]).ToArray();
                var finding = matchingFindings.FirstOrDefault();
                var observation = new AttackCoverageObservation(
                    1,
                    "sensor-" + Guid.NewGuid().ToString("N"),
                    boundary.Id,
                    attack.Entry.Id,
                    verdict,
                    verdict == AttackCoverageVerdict.Finding
                        ? "The boundary inventory produced an authoritative mechanical finding."
                        : "The boundary inventory completed the applicable mechanical checks without a finding.",
                    evidence,
                    inputs,
                    finding?.Id,
                    finding?.Fingerprint,
                    AttackCoverageSource.DeterministicSensor,
                    new AttackReviewerIdentity("boundary-analyzer", inventory.SensorVersion, "deterministic"),
                    DeterministicPromptVersion,
                    DeterministicPromptHash,
                    catalogue.Version,
                    attack.EntryHash,
                    snapshot.BoundaryDefinitionHash,
                    snapshot.CoveredCodeHash,
                    new AttackTokenCost(0, 0),
                    clock().ToUniversalTime(),
                    commit,
                    null);
                await ledger.AppendAsync(observation, cancellationToken).ConfigureAwait(false);
                appended.Add(observation);
            }
        }
        return appended;
    }

    private static async Task EnsureFindingLifecycleLinkAsync(
        string repositoryRoot,
        BoundaryEntry boundary,
        AttackCatalogueEntry attack,
        AttackJudgementSubmission submission,
        string basisObservationId,
        CancellationToken cancellationToken)
    {
        var store = new FindingStateStore(repositoryRoot);
        var states = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var priorObservations = await new AttackCoverageLedger(repositoryRoot).ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        var previous = priorObservations.Where(observation =>
                observation.BoundaryId == boundary.Id &&
                observation.AttackId == attack.Id &&
                observation.Source != AttackCoverageSource.DeterministicSensor &&
                observation.Verdict == AttackCoverageVerdict.Finding)
            .Select(observation =>
            {
                if (observation.FindingFingerprint is not null)
                    return new FindingIdentityRecord(
                        observation.FindingFingerprint,
                        observation.FindingId ?? "finding-" + observation.FindingFingerprint[7..],
                        boundary.Location.Path,
                        attack.Id);
                return states.Values.FirstOrDefault(record =>
                    record.FindingId == observation.FindingId) is { } state
                    ? new FindingIdentityRecord(
                        state.Fingerprint, state.FindingId, state.Path, state.RuleId)
                    : null;
            })
            .Where(item => item is not null)
            .Cast<FindingIdentityRecord>()
            .DistinctBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var current = new List<FindingIdentityRecord>();
        if (submission.Verdict == AttackCoverageVerdict.Finding &&
            string.IsNullOrWhiteSpace(submission.FindingFingerprint))
        {
            if (string.IsNullOrWhiteSpace(submission.FindingId) ||
                states.Values.FirstOrDefault(record => record.FindingId == submission.FindingId) is not { } linked)
                throw new ArgumentException(
                    "A finding verdict must supply a fingerprint or link an existing finding id.",
                    nameof(submission));
            current.Add(new FindingIdentityRecord(
                linked.Fingerprint, linked.FindingId, linked.Path, linked.RuleId));
        }
        else if (submission.Verdict == AttackCoverageVerdict.Finding)
        {
            if (!IsSha256(submission.FindingFingerprint!))
                throw new ArgumentException("Finding fingerprint must be a sha256 value.", nameof(submission));
            if (states.TryGetValue(submission.FindingFingerprint!, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(submission.FindingId) &&
                    existing.FindingId != submission.FindingId)
                    throw new ArgumentException("Finding id does not match the linked fingerprint.", nameof(submission));
                current.Add(new FindingIdentityRecord(
                    existing.Fingerprint, existing.FindingId, existing.Path, existing.RuleId));
            }
            else
            {
                var findingId = string.IsNullOrWhiteSpace(submission.FindingId)
                    ? "finding-" + submission.FindingFingerprint![7..]
                    : submission.FindingId;
                current.Add(new FindingIdentityRecord(
                    submission.FindingFingerprint!, findingId, boundary.Location.Path, attack.Id));
            }
        }
        if (current.Count > 0 || previous.Length > 0)
            await store.MergeReviewAsync(
                current, previous, submission.Reviewer.Agent, cancellationToken).ConfigureAwait(false);
        if (submission.Verdict == AttackCoverageVerdict.Pass && previous.Length > 0)
        {
            var latestStates = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var finding in previous)
            {
                if (latestStates.TryGetValue(finding.Fingerprint, out var state) && state.State != FindingState.Resolved)
                {
                    await store.ResolveAsync(
                        finding.Fingerprint,
                        submission.Reviewer.Agent,
                        "An explicit attack-coverage pass reconciled the prior finding.",
                        [basisObservationId],
                        "attack-coverage-pass-reconciliation@1",
                        state.Timestamp,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool IsSha256(string value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AttackCoverageCell ProjectCell(
        string boundaryId,
        ResolvedAttackCatalogueEntry attack,
        BoundaryCoverageSnapshot snapshot,
        PromptReference prompt,
        IReadOnlyList<AttackCoverageAssessmentHistory> history,
        DateTimeOffset now)
    {
        if (history.Count == 0)
        {
            return new AttackCoverageCell(
                boundaryId, attack.Entry.Id, AttackCoverageVerdict.NotYetChecked,
                "No judgement has been recorded. This is an explicit work item, not a pass.",
                [], null, null, false, false, true,
                RequiredJudgements(attack.Entry), 0, "none", null, null, [], [], history);
        }

        var latest = history[^1];
        var judgements = latest.Judgements.ToList();
        var authoritativeSensor = history.SelectMany(item => item.Judgements)
            .Where(item =>
                item.Source == AttackCoverageSource.DeterministicSensor &&
                item.BoundaryDefinitionHash == snapshot.BoundaryDefinitionHash &&
                item.CoveredCodeHash == snapshot.CoveredCodeHash &&
                item.CatalogueEntryHash == attack.EntryHash &&
                item.PromptHash == DeterministicPromptHash)
            .OrderBy(item => item.CheckedAt)
            .LastOrDefault();
        if (authoritativeSensor is not null &&
            judgements.All(item => !ReferenceEquals(item, authoritativeSensor)))
            judgements.Add(authoritativeSensor);
        var deterministic = judgements.Where(item => item.Source == AttackCoverageSource.DeterministicSensor).ToArray();
        IReadOnlyList<AttackCoverageObservation> selected = deterministic.Length > 0 ? deterministic : judgements;
        var deterministicOverride = deterministic.Length > 0 &&
            judgements.Any(item => item.Source != AttackCoverageSource.DeterministicSensor &&
                                   item.Verdict != deterministic[^1].Verdict);
        var disagreement = selected.Select(item => item.Verdict).Distinct().Count() > 1 ||
                           deterministicOverride ||
                           (deterministic.Length == 0 && judgements.Select(item => item.Verdict).Distinct().Count() > 1);
        var independent = selected.Select(item =>
                $"{item.Reviewer.Agent}\0{item.Reviewer.Model}\0{item.Reviewer.ThinkingLevel}")
            .Distinct(StringComparer.Ordinal).Count();
        var required = deterministic.Length > 0 ? 1 : RequiredJudgements(attack.Entry);
        var complete = independent >= required;
        var verdict = ResolveVerdict(selected);
        var reason = string.Join(" ", selected.Select(item => item.Reasoning).Distinct(StringComparer.Ordinal));
        if (!complete)
        {
            verdict = AttackCoverageVerdict.NotYetChecked;
            reason = $"Awaiting {required - independent} additional independent judgement(s). " + reason;
        }
        else if (disagreement)
        {
            reason = (deterministic.Length > 0
                ? "A deterministic sensor overrides a contradicting judgement. "
                : "Independent judgements disagree; the conservative visible verdict is retained. ") + reason;
        }

        var staleness = Staleness(selected, snapshot, attack, prompt);
        var checkedAt = selected.Max(item => item.CheckedAt);
        var finding = selected.FirstOrDefault(item => item.Verdict == AttackCoverageVerdict.Finding);
        return new AttackCoverageCell(
            boundaryId,
            attack.Entry.Id,
            verdict,
            reason,
            selected.SelectMany(item => item.Evidence).Distinct().ToArray(),
            finding?.FindingId,
            finding?.FindingFingerprint,
            disagreement,
            deterministicOverride,
            disagreement || staleness.Count > 0 || !complete,
            required,
            independent,
            !complete || disagreement ? "low" : deterministic.Length > 0 ? "mechanical" : independent > 1 ? "corroborated" : "single-judgement",
            checkedAt,
            Math.Max(0, (now - checkedAt).TotalDays),
            staleness,
            judgements,
            history);
    }

    private static IReadOnlyList<AttackCoverageStalenessReason> Staleness(
        IReadOnlyList<AttackCoverageObservation> observations,
        BoundaryCoverageSnapshot snapshot,
        ResolvedAttackCatalogueEntry attack,
        PromptReference prompt)
    {
        var reasons = new List<AttackCoverageStalenessReason>();
        if (observations.Any(item => item.BoundaryDefinitionHash != snapshot.BoundaryDefinitionHash))
            reasons.Add(AttackCoverageStalenessReason.BoundaryChanged);
        if (observations.Any(item => item.CoveredCodeHash != snapshot.CoveredCodeHash))
            reasons.Add(AttackCoverageStalenessReason.CodeChanged);
        if (observations.Any(item => item.CatalogueEntryHash != attack.EntryHash))
            reasons.Add(AttackCoverageStalenessReason.CatalogueChanged);
        if (observations.Any(item =>
                item.PromptHash != (item.Source == AttackCoverageSource.DeterministicSensor
                    ? DeterministicPromptHash
                    : prompt.ContentHash)))
            reasons.Add(AttackCoverageStalenessReason.PromptChanged);
        return reasons;
    }

    private static AttackCoverageAssessmentHistory ProjectHistory(
        IReadOnlyList<AttackCoverageObservation> judgements)
    {
        var deterministic = judgements.Where(item => item.Source == AttackCoverageSource.DeterministicSensor).ToArray();
        var selected = deterministic.Length > 0 ? deterministic : judgements;
        var disagreement = judgements.Select(item => item.Verdict).Distinct().Count() > 1;
        return new AttackCoverageAssessmentHistory(
            judgements[0].AssessmentId,
            judgements.Max(item => item.CheckedAt),
            ResolveVerdict(selected),
            disagreement,
            judgements,
            judgements.Select(item => item.Commit).LastOrDefault(value => value is not null),
            judgements.Select(item => item.CommitRange).LastOrDefault(value => value is not null));
    }

    private static AttackCoverageVerdict ResolveVerdict(IReadOnlyList<AttackCoverageObservation> judgements)
    {
        if (judgements.Any(item => item.Verdict == AttackCoverageVerdict.Finding))
            return AttackCoverageVerdict.Finding;
        var distinct = judgements.Select(item => item.Verdict).Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0] : AttackCoverageVerdict.NotYetChecked;
    }

    private static int RequiredJudgements(AttackCatalogueEntry entry) =>
        entry.Severity is AttackSeverity.Critical or AttackSeverity.High ? 2 : 1;

    private static bool FindingTargetsBoundary(ReviewFinding finding, BoundaryEntry boundary)
    {
        if (finding.Evidence is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(finding.Evidence);
                if (document.RootElement.TryGetProperty("boundaryId", out var id) &&
                    id.GetString() == boundary.Id) return true;
            }
            catch (JsonException)
            {
                // Fall through to exact source location matching.
            }
        }
        return finding.Locations.Any(location =>
            location.Path == boundary.Location.Path &&
            location.Range?.Start.Line == boundary.Location.Line);
    }

    private static async Task<string?> GitAsync(string repositoryRoot, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = Path.GetFullPath(repositoryRoot),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
