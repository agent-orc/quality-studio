using System.Collections.Concurrent;
using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RepositorySensorAvailabilityMeasurement(
    IReadOnlyDictionary<string, SensorAvailability> Availability,
    bool CacheHit,
    double RepositoryStateMilliseconds,
    double InitializationMilliseconds);

/// <summary>Keeps one sensor-availability result per repository state and registry entry.</summary>
public sealed class RepositorySensorAvailabilityCache
{
    private readonly ConcurrentDictionary<string, CacheSlot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly RepositoryHierarchyCache hierarchyCache;

    public RepositorySensorAvailabilityCache(RepositoryHierarchyCache hierarchyCache) =>
        this.hierarchyCache = hierarchyCache;

    public void Seed(
        RepositoryRegistration registration,
        string repositoryState,
        IReadOnlyDictionary<string, SensorAvailability> availability)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryState);
        ArgumentNullException.ThrowIfNull(availability);
        var slot = slots.GetOrAdd(registration.Id, _ => new CacheSlot());
        slot.Gate.Wait();
        try
        {
            slot.Key = RepositoryCacheState.CombinedKey(repositoryState, registration);
            slot.Availability = new Dictionary<string, SensorAvailability>(
                availability, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task<RepositorySensorAvailabilityMeasurement> GetAsync(
        RepositoryRegistration registration,
        SensorRegistry sensors,
        CancellationToken cancellationToken)
    {
        var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
            ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
            : registration.GlobalInputsDirectory;
        var state = hierarchyCache.MeasureState(
            registration.RootPath,
            globalDirectory,
            registration.InputBudgetCharacters);
        var key = RepositoryCacheState.CombinedKey(state.State, registration);
        var slot = slots.GetOrAdd(registration.Id, _ => new CacheSlot());
        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            if (slot.Availability is not null && StringComparer.Ordinal.Equals(slot.Key, key))
            {
                return new RepositorySensorAvailabilityMeasurement(
                    slot.Availability,
                    true,
                    state.DurationMilliseconds,
                    0);
            }

            var started = Stopwatch.GetTimestamp();
            var availability = new Dictionary<string, SensorAvailability>(StringComparer.OrdinalIgnoreCase);
            foreach (var sensor in sensors.List())
            {
                availability[sensor.Id] = await sensor.ProbeAvailabilityAsync(cancellationToken);
            }
            slot.Key = key;
            slot.Availability = availability;
            return new RepositorySensorAvailabilityMeasurement(
                availability,
                false,
                state.DurationMilliseconds,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private sealed class CacheSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Key { get; set; }
        public IReadOnlyDictionary<string, SensorAvailability>? Availability { get; set; }
    }
}
