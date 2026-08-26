using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationAppendResult(string Path, bool Appended, string ObservationId);

/// <summary>Append-only, repository-local history for immutable quality observations.</summary>
public static class QualityObservationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations",
            timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task<QualityObservationAppendResult> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var serialized = QualityObservationJson.SerializeCompact(observation);
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && await ContainsAsync(path, observation.ObservationId, cancellationToken)
                    .ConfigureAwait(false))
                return new QualityObservationAppendResult(path, false, observation.ObservationId);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(serialized + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new QualityObservationAppendResult(path, true, observation.ObservationId);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<QualityObservationDocument>> ReadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var result = new List<QualityObservationDocument>();
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory)) return result;

        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var read = QualityObservationJson.Read(line);
                    if (read.IsSupported && read.Observation is not null) result.Add(read.Observation);
                }
                catch (JsonException)
                {
                    // A partial or corrupt line must not hide later immutable observations.
                }
            }
        }
        return result;
    }

    private static async Task<bool> ContainsAsync(
        string path,
        string observationId,
        CancellationToken cancellationToken)
    {
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var parsed = JsonDocument.Parse(line);
                if (parsed.RootElement.TryGetProperty("observationId", out var id) &&
                    string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // Preserve malformed history and continue looking for a matching valid id.
            }
        }
        return false;
    }
}

public sealed class QualityTaxonomyOptions
{
    public const string SectionName = "QualityTaxonomy";
    public bool ObservationWriteEnabled { get; set; }
    public bool ObservationReadEnabled { get; set; }
}
