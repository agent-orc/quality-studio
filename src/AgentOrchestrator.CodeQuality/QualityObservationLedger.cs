using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Append-only, repository-local store for immutable quality observations.</summary>
public static class QualityObservationLedger
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions LineJsonOptions = new(QualityObservationJson.Options)
    {
        WriteIndented = false,
    };

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset observedAt) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations",
            observedAt.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(observation);
        _ = QualityObservationJson.Serialize(observation);
        var serialized = JsonSerializer.Serialize(observation, LineJsonOptions);
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && await ContainsAsync(path, observation.ObservationId, cancellationToken)
                    .ConfigureAwait(false))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(serialized + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<QualityObservationDocument>> QueryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory)) return [];
        var observations = new List<QualityObservationDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var result = QualityObservationJson.Read(line);
                    if (result.Supported && result.Document is not null) observations.Add(result.Document);
                }
                catch (JsonException)
                {
                    // A partial or unsupported historical line must not hide later valid observations.
                }
            }
        }
        return observations;
    }

    public static string CreateObservationId(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string inputHash,
        string taxonomyDigest)
    {
        var canonical = new StringBuilder("quality-studio-observation-id-v1\0")
            .Append(runId).Append('\0')
            .Append(unitId).Append('\0')
            .Append(kind).Append('\0')
            .Append(subjectHash).Append('\0')
            .Append(inputHash).Append('\0')
            .Append(taxonomyDigest);
        return "observation-sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
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
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("observationId", out var id) &&
                    string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // Keep scanning after a malformed historical line.
            }
        }
        return false;
    }
}
