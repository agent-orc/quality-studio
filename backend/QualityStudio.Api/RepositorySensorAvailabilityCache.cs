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
            var configured = (registration.Sensors ?? [])
                .GroupBy(sensor => sensor.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Configuration, StringComparer.OrdinalIgnoreCase);
            foreach (var sensor in sensors.List())
            {
                // An analyzer's tools belong to the repository (its global.json, its node_modules), so
                // it is probed there and in its profile's working directory, not in the host's.
                try
                {
                    availability[sensor.Id] = sensor is IRepositoryProbedSensor probed
                        ? await probed.ProbeAvailabilityAsync(
                            registration.RootPath, configured.GetValueOrDefault(sensor.Id), cancellationToken)
                        : await sensor.ProbeAvailabilityAsync(cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // One probe that throws (an unreadable config, a broken tool) makes that sensor
                    // unavailable with the reason; it must not fail the sensor list of the repository.
                    availability[sensor.Id] = new SensorAvailability(
                        false, $"{sensor.Id} is unavailable: its availability probe failed: {exception.Message}");
                }
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
