using System.Collections.Concurrent;
using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

/// <summary>
/// Reuses sensor availability probes for a while. Each probe starts a real tool process, so one
/// unthrottled GET /api/sensors used to spawn roughly one process per sensor, on every call and for
/// every repository. The answer changes only when the host's toolchain changes.
/// </summary>
public sealed class SensorAvailabilityCache
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan ttl;

    public SensorAvailabilityCache(IOptions<RepositoryOptions> options) =>
        ttl = TimeSpan.FromSeconds(options.Value.Limits.SensorAvailabilityCacheSeconds);

    public TimeSpan Ttl => ttl;

    public async Task<SensorAvailability> ProbeAsync(IReviewSensor sensor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        if (ttl <= TimeSpan.Zero) return await sensor.ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        // Concurrent callers share one in-flight probe rather than starting the tool once each.
        var entry = entries.AddOrUpdate(
            sensor.Id,
            _ => Start(sensor, cancellationToken),
            (_, existing) => IsFresh(existing) ? existing : Start(sensor, cancellationToken));
        try
        {
            return await entry.Probe.ConfigureAwait(false);
        }
        catch
        {
            // A failed probe must not be cached as an answer; the next request asks the tool again.
            entries.TryRemove(new KeyValuePair<string, Entry>(sensor.Id, entry));
            throw;
        }
    }

    private static Entry Start(IReviewSensor sensor, CancellationToken cancellationToken) =>
        new(sensor.ProbeAvailabilityAsync(cancellationToken), Stopwatch.GetTimestamp());

    private bool IsFresh(Entry entry) =>
        !entry.Probe.IsFaulted && !entry.Probe.IsCanceled &&
        Stopwatch.GetElapsedTime(entry.Timestamp) < ttl;

    private sealed record Entry(Task<SensorAvailability> Probe, long Timestamp);
}
