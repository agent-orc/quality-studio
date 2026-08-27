using System.Globalization;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Analysis;

namespace QualityStudio.Api;

/// <summary>
/// Adapts the stable analysis-core result to API response and review-evidence contracts.
/// HTTP routing, repository authorization, and response projection stay in the API host.
/// </summary>
public sealed class AnalysisCoreApiAdapter(
    QualityAnalysisRunner runner,
    SensorRegistry sensors)
{
    public async Task<SensorScanResult> RunSensorAsync(
        RepositoryRegistration repository,
        RepositorySensorConfiguration configuration,
        SensorScope scope,
        string? path,
        bool persistMetadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(configuration);

        var result = await runner.RunAsync(new QualityAnalysisRequest(
            repository.RootPath,
            [new QualityAnalysisDefinition(configuration.Id, configuration.Configuration, scope, path)],
            repository.Id,
            persistMetadata), cancellationToken).ConfigureAwait(false);

        return ToSensorScanResult(result);
    }

    public async Task<IReadOnlyList<SensorScanResult>> CollectDeterministicEvidenceAsync(
        RepositoryRegistration repository,
        IReadOnlyList<RepositorySensorConfiguration> configurations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(configurations);

        var tasks = configurations
            .Where(configuration => configuration.Enabled)
            .DistinctBy(configuration => configuration.Id, StringComparer.OrdinalIgnoreCase)
            .Select(configuration => CollectOneAsync(repository, configuration, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderBy(result => result.Provenance.SensorId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<SensorScanResult?> CollectOneAsync(
        RepositoryRegistration repository,
        RepositorySensorConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IReviewSensor sensor;
        try
        {
            sensor = sensors.Get(configuration.Id);
        }
        catch (SensorNotFoundException)
        {
            return null;
        }

        if (sensor is not IDeterministicEvidenceSensor) return null;

        try
        {
            var result = await RunSensorAsync(
                repository,
                configuration,
                SensorScope.Repository,
                path: null,
                persistMetadata: false,
                cancellationToken).ConfigureAwait(false);
            if (result.Findings.Any(finding =>
                    finding.Source?.Kind != FindingSourceKind.Deterministic ||
                    string.IsNullOrWhiteSpace(finding.Source.SensorId)))
            {
                throw new InvalidDataException(
                    $"Deterministic sensor '{sensor.Id}' returned a finding without deterministic source provenance.");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SensorScanResult(
                false,
                $"Analyzer execution failed: {exception.Message}",
                [],
                new SensorProvenance(
                    sensor.Id,
                    sensor.Version,
                    "repository",
                    ".",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }

    private static SensorScanResult ToSensorScanResult(QualityAnalysisResult result)
    {
        var execution = result.Analyses.Single();
        return new SensorScanResult(
            execution.Available,
            execution.UnavailableReason,
            result.Findings.Select(finding => new ReviewFinding(
                finding.Id,
                finding.Aspect,
                finding.Severity,
                finding.Title,
                finding.Description,
                finding.Recommendation,
                finding.Locations,
                finding.Fingerprint,
                finding.RuleId,
                finding.Evidence,
                new FindingSource(
                    FindingSourceKind.Deterministic,
                    execution.Name,
                    finding.Producer.Id,
                    finding.Producer.Version))).ToArray(),
            execution.Provenance);
    }
}
