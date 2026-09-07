using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRunReportPinDocument(int SchemaVersion, IReadOnlyList<string> PinnedRunIds);

/// <summary>
/// Persists which run outcomes an operator has pinned as durable comparison baselines, so retention
/// pruning never deletes them. Pins are repository-scoped, same as the outcome snapshots themselves.
/// </summary>
public sealed class QualityRunReportPinStore
{
    /// <summary>Where pins lived inside the checkout before QS-102. Only the migration reads it.</summary>
    public const string LegacyRelativePath = ".quality/reports/pins.json";

    /// <summary>Where pins live, below the project's data root.</summary>
    public const string DataRelativePath = "reports/pins.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly object gate = new();

    public QualityRunReportPinStore(string repositoryRoot)
        : this(QualityWorkspace.ForRepository(repositoryRoot))
    {
    }

    public QualityRunReportPinStore(QualityWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        path = workspace.Combine(DataRelativePath);
    }

    public IReadOnlySet<string> Load()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            var document = JsonSerializer.Deserialize<QualityRunReportPinDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Review run pin file is empty: {path}");
            return new HashSet<string>(document.PinnedRunIds, StringComparer.Ordinal);
        }
    }

    public IReadOnlySet<string> Pin(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (gate)
        {
            var pinned = new HashSet<string>(Load(), StringComparer.Ordinal) { runId };
            Persist(pinned);
            return pinned;
        }
    }

    public IReadOnlySet<string> Unpin(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (gate)
        {
            var pinned = new HashSet<string>(Load(), StringComparer.Ordinal);
            pinned.Remove(runId);
            Persist(pinned);
            return pinned;
        }
    }

    private void Persist(IReadOnlySet<string> pinned)
    {
        var document = new QualityRunReportPinDocument(1, pinned.Order(StringComparer.Ordinal).ToArray());
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
    }
}
