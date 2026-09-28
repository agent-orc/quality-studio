namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Which enabled sensors a review run executes, and what that costs in wall-clock time.
/// <para>
/// A sensor that builds the analysed project - <c>dotnet-build</c>, <c>angular-compiler</c> - is
/// never run as a side effect of a review. Enabling it on the repository makes it available; a run
/// executes it only when the operator opts in for that run. Every other enabled sensor runs, or is
/// served from <see cref="SensorResultCache"/> when the working copy has not changed.
/// </para>
/// </summary>
public static class SensorExecutionPolicy
{
    /// <summary>Sensors that compile the analysed project and therefore run only by per-run opt-in.</summary>
    public static IReadOnlyList<string> OptInSensorIds { get; } = ["angular-compiler", "dotnet-build"];

    /// <summary>Used until a sensor has run once in the repository and left an observed duration.</summary>
    private static readonly IReadOnlyDictionary<string, long> DefaultDurationsMs =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet-build"] = 180_000,
            ["roslyn"] = 180_000,
            ["angular-compiler"] = 90_000,
            ["eslint"] = 60_000,
            ["sarif"] = 60_000,
            ["tsc"] = 45_000,
            ["gitleaks"] = 30_000,
            ["dependencies"] = 20_000,
            ["architecture"] = 10_000,
            ["boundaries"] = 10_000,
            ["coverage"] = 5_000,
        };

    private const long UnknownDurationMs = 30_000;

    public static bool RequiresOptIn(string sensorId) =>
        OptInSensorIds.Contains(sensorId, StringComparer.OrdinalIgnoreCase);

    public static long DefaultDurationMs(string sensorId) =>
        DefaultDurationsMs.GetValueOrDefault(sensorId, UnknownDurationMs);

    /// <summary>
    /// The enabled sensors of <paramref name="configured"/> this run may execute: everything except an
    /// opt-in sensor the run did not name.
    /// </summary>
    public static IReadOnlyList<ReviewSensorConfiguration> Admit(
        IEnumerable<ReviewSensorConfiguration> configured,
        IReadOnlyCollection<string>? optIn) =>
        configured.Where(sensor => !RequiresOptIn(sensor.Id) ||
                                   (optIn ?? []).Contains(sensor.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
}

/// <summary>Why a sensor is part of a review: evidence for every kind, or the security bundle.</summary>
public static class SensorRole
{
    public const string Deterministic = "deterministic";
    public const string Security = "security";
}

/// <summary>What the launcher tells the operator about one enabled sensor before a run starts.</summary>
public sealed record SensorPlanEntry(
    string SensorId,
    string Role,
    string Decision,
    bool OptIn,
    bool OptedIn,
    long ExpectedDurationMs,
    string DurationSource,
    string? Detail = null);

/// <summary>The sensors of one prospective run and the wall-clock time they are expected to take.</summary>
public sealed record SensorExecutionPlan(
    IReadOnlyList<SensorPlanEntry> Sensors,
    long ExpectedDurationMs,
    string? InputFingerprint,
    string? CacheNote)
{
    /// <summary>
    /// Plans the sensors a run with these settings would execute. Sensors run concurrently, so the
    /// expected duration of the whole phase is that of its slowest sensor, not their sum.
    /// </summary>
    public static SensorExecutionPlan Build(
        string repositoryRoot,
        IReadOnlyList<(ReviewSensorConfiguration Configuration, IReviewSensor Sensor, string Role)> enabled,
        IReadOnlyCollection<string>? optIn,
        SensorResultCache cache)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(cache);
        var entries = new List<SensorPlanEntry>(enabled.Count);
        foreach (var (configuration, sensor, role) in enabled
                     .DistinctBy(item => item.Configuration.Id, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Configuration.Id, StringComparer.Ordinal))
        {
            var requiresOptIn = SensorExecutionPolicy.RequiresOptIn(sensor.Id);
            var optedIn = requiresOptIn && (optIn ?? []).Contains(sensor.Id, StringComparer.OrdinalIgnoreCase);
            var observed = SensorResultCache.LastObservedDurationMs(repositoryRoot, sensor.Id);
            var expected = observed ?? SensorExecutionPolicy.DefaultDurationMs(sensor.Id);
            var source = observed is null ? "default" : "observed";
            if (requiresOptIn && !optedIn)
            {
                entries.Add(new SensorPlanEntry(sensor.Id, role, SensorOutcome.OptInRequired, true, false,
                    expected, source, "Builds the project; runs only when this run opts in."));
                continue;
            }
            if (cache.TryGet(sensor, configuration.Configuration) is { } cached)
            {
                entries.Add(new SensorPlanEntry(sensor.Id, role, SensorOutcome.Cached, requiresOptIn, optedIn,
                    0, "cache", $"Reuses the result stored {cached.StoredAt:yyyy-MM-dd HH:mm} UTC for this commit and inputs."));
                continue;
            }
            entries.Add(new SensorPlanEntry(sensor.Id, role, "run", requiresOptIn, optedIn, expected, source));
        }

        return new SensorExecutionPlan(
            entries,
            entries.Where(entry => entry.Decision == "run").Select(entry => entry.ExpectedDurationMs)
                .DefaultIfEmpty(0).Max(),
            cache.Fingerprint?.Value,
            cache.Fingerprint is null
                ? "The repository has no Git commit to key results to; every admitted sensor runs."
                : cache.Fingerprint.DirtyFiles > 0
                    ? $"Keyed to {cache.Fingerprint.Head[..Math.Min(12, cache.Fingerprint.Head.Length)]} plus {cache.Fingerprint.DirtyFiles} uncommitted file(s)."
                    : $"Keyed to {cache.Fingerprint.Head[..Math.Min(12, cache.Fingerprint.Head.Length)]}.");
    }
}
