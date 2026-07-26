using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum AttackCoverageVerdict
{
    Pass,
    Finding,
    NotApplicable,
    NotYetChecked,
}

public enum AttackCoverageSource
{
    Agent,
    DeterministicSensor,
    Human,
}

public enum AttackCoverageStalenessReason
{
    BoundaryChanged,
    CodeChanged,
    CatalogueChanged,
    PromptChanged,
}

public enum AttackSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

public sealed record AttackApplicability(
    IReadOnlyList<string> BoundaryKinds,
    IReadOnlyList<string>? Directions = null);

public sealed record AttackCatalogueEntry(
    string Id,
    string Version,
    string Title,
    string Description,
    AttackApplicability Applicability,
    IReadOnlyList<string> EvidenceRequirements,
    AttackSeverity Severity,
    string SeverityFrame,
    IReadOnlyList<string> DeterministicRuleIds,
    bool DeterministicPassConclusive = false,
    bool Enabled = true);

public sealed record AttackCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string CatalogueVersion,
    IReadOnlyList<AttackCatalogueEntry> Entries);

public sealed record ResolvedAttackCatalogueEntry(
    AttackCatalogueEntry Entry,
    string Scope,
    string Source,
    string SourceCatalogueVersion,
    string EntryHash);

public sealed record ResolvedAttackCatalogue(
    string Version,
    IReadOnlyList<ResolvedAttackCatalogueEntry> Entries,
    IReadOnlyList<string> Sources);

public sealed record AttackReviewerIdentity(
    string Agent,
    string Model,
    string ThinkingLevel);

public sealed record AttackTokenCost(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens = 0,
    long ReasoningOutputTokens = 0)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record AttackEvidence(
    string Kind,
    string Reference,
    string Summary);

/// <summary>
/// One immutable judgement. Judgements sharing an assessment id are independent
/// votes over the same exact input and form one point in the cell trajectory.
/// </summary>
public sealed record AttackCoverageObservation(
    int SchemaVersion,
    string AssessmentId,
    string BoundaryId,
    string AttackId,
    AttackCoverageVerdict Verdict,
    string Reasoning,
    IReadOnlyList<AttackEvidence> Evidence,
    IReadOnlyList<string> DeterministicSensorInput,
    string? FindingId,
    string? FindingFingerprint,
    AttackCoverageSource Source,
    AttackReviewerIdentity Reviewer,
    string PromptVersion,
    string PromptHash,
    string CatalogueVersion,
    string CatalogueEntryHash,
    string BoundaryDefinitionHash,
    string CoveredCodeHash,
    AttackTokenCost TokenCost,
    DateTimeOffset CheckedAt,
    string? Commit,
    string? CommitRange);

public sealed record AttackCoverageAssessmentHistory(
    string AssessmentId,
    DateTimeOffset CheckedAt,
    AttackCoverageVerdict Verdict,
    bool Disagreement,
    IReadOnlyList<AttackCoverageObservation> Judgements,
    string? Commit,
    string? CommitRange);

public sealed record AttackCoverageCell(
    string BoundaryId,
    string AttackId,
    AttackCoverageVerdict Verdict,
    string Reason,
    IReadOnlyList<AttackEvidence> Evidence,
    string? FindingId,
    string? FindingFingerprint,
    bool Disagreement,
    bool DeterministicOverride,
    bool NeedsHumanAttention,
    int RequiredJudgements,
    int IndependentJudgements,
    string Confidence,
    DateTimeOffset? CheckedAt,
    double? AgeDays,
    IReadOnlyList<AttackCoverageStalenessReason> StalenessReasons,
    IReadOnlyList<AttackCoverageObservation> Provenance,
    IReadOnlyList<AttackCoverageAssessmentHistory> History);

public sealed record AttackCoverageRow(
    BoundaryEntry Boundary,
    string BoundaryDefinitionHash,
    string CoveredCodeHash,
    int CodeChangeCount,
    DateTimeOffset? OldestVerdictAt,
    IReadOnlyList<AttackCoverageCell> Cells);

public sealed record AttackCoverageMatrix(
    int SchemaVersion,
    string CatalogueVersion,
    string PromptVersion,
    string PromptHash,
    DateTimeOffset GeneratedAt,
    string Scope,
    IReadOnlyList<AttackCatalogueEntry> Attacks,
    IReadOnlyList<AttackCoverageRow> Rows)
{
    public int CellCount => Rows.Sum(row => row.Cells.Count);
    public int NotYetCheckedCount => Rows.Sum(row => row.Cells.Count(cell => cell.Verdict == AttackCoverageVerdict.NotYetChecked));
    public int StaleCount => Rows.Sum(row => row.Cells.Count(cell => cell.StalenessReasons.Count > 0));
    public int DisagreementCount => Rows.Sum(row => row.Cells.Count(cell => cell.Disagreement));
}

public sealed record AttackJudgementSubmission(
    string? AssessmentId,
    string BoundaryId,
    string AttackId,
    AttackCoverageVerdict Verdict,
    string Reasoning,
    IReadOnlyList<AttackEvidence> Evidence,
    IReadOnlyList<string>? DeterministicSensorInput,
    string? FindingId,
    string? FindingFingerprint,
    AttackCoverageSource Source,
    AttackReviewerIdentity Reviewer,
    AttackTokenCost TokenCost,
    string? Commit,
    string? CommitRange);

public static class AttackCoverageJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Hash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public sealed class AttackCatalogueResolver
{
    public const string ProjectRelativePath = ".quality/attacks/catalogue.json";
    public const string GlobalFileName = "attack-catalogue.json";
    private const string BuiltInResourceSuffix = "catalogues.attack-catalogue.v1.json";

    public ResolvedAttackCatalogue Resolve(string repositoryRoot, string? globalInputsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var documents = new List<(AttackCatalogueDocument Document, string Scope, string Source)>
        {
            (ReadBuiltIn(), "built-in", "embedded:" + BuiltInResourceSuffix),
        };
        if (!string.IsNullOrWhiteSpace(globalInputsDirectory))
        {
            var globalPath = Path.Combine(Path.GetFullPath(globalInputsDirectory), GlobalFileName);
            if (File.Exists(globalPath)) documents.Add((ReadFile(globalPath), "global", globalPath));
        }
        var projectPath = Path.Combine(Path.GetFullPath(repositoryRoot),
            ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(projectPath)) documents.Add((ReadFile(projectPath), "project", projectPath));

        var effective = new Dictionary<string, ResolvedAttackCatalogueEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (document, scope, source) in documents)
        {
            Validate(document, source);
            foreach (var entry in document.Entries)
            {
                effective[entry.Id] = new ResolvedAttackCatalogueEntry(
                    entry, scope, source, document.CatalogueVersion, AttackCoverageJson.Hash(entry));
            }
        }

        var version = string.Join("|", documents.Select(item =>
            $"{item.Scope}:{item.Document.CatalogueVersion}"));
        return new ResolvedAttackCatalogue(
            version,
            effective.Values.Where(item => item.Entry.Enabled)
                .OrderBy(item => item.Entry.Id, StringComparer.Ordinal).ToArray(),
            documents.Select(item => item.Source).ToArray());
    }

    public static bool Applies(AttackCatalogueEntry entry, BoundaryEntry boundary) =>
        (entry.Applicability.BoundaryKinds.Contains("*", StringComparer.OrdinalIgnoreCase) ||
         entry.Applicability.BoundaryKinds.Contains(boundary.Kind, StringComparer.OrdinalIgnoreCase)) &&
        (entry.Applicability.Directions is null ||
         entry.Applicability.Directions.Contains(boundary.Direction, StringComparer.OrdinalIgnoreCase));

    private static AttackCatalogueDocument ReadBuiltIn()
    {
        var assembly = typeof(AttackCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in attack catalogue is unavailable.");
        return JsonSerializer.Deserialize<AttackCatalogueDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException("The built-in attack catalogue is empty.");
    }

    private static AttackCatalogueDocument ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AttackCatalogueDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException($"Attack catalogue '{path}' is empty.");
    }

    private static void Validate(AttackCatalogueDocument document, string source)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.CatalogueVersion) ||
            document.Entries is null)
            throw new JsonException($"Attack catalogue '{source}' has an unsupported contract.");
        if (document.Entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException($"Attack catalogue '{source}' contains duplicate ids.");
        if (document.Entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version) ||
                string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Description) ||
                entry.Applicability?.BoundaryKinds is not { Count: > 0 } ||
                entry.EvidenceRequirements is not { Count: > 0 } ||
                string.IsNullOrWhiteSpace(entry.SeverityFrame)))
            throw new JsonException($"Attack catalogue '{source}' contains an invalid entry.");
    }
}

public static class AttackCoveragePrompt
{
    public const string Version = "attack-coverage-review.v1";
    private const string ResourceSuffix = "prompts.attack-coverage-review.v1.md";

    public static PromptReference Reference()
    {
        var assembly = typeof(AttackCoveragePrompt).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The attack coverage prompt is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        return new PromptReference(
            "attack-coverage-review",
            Version,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
    }
}
