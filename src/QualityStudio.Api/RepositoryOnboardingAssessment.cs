using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public static class RepositoryOnboardingStatuses
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Block = "block";
    public const string Unavailable = "unavailable";
    public const string Skipped = "skipped";
}

public sealed record RepositoryOnboardingCheck(
    string Status,
    bool Available,
    int FindingCount,
    string Summary,
    IReadOnlyDictionary<string, string> ToolVersions);

public sealed record RepositoryOnboardingSecretFinding(
    string RuleId,
    string Severity,
    string Title,
    string Path,
    int? Line);

public sealed record RepositoryDependencyAdvisory(
    string AdvisoryId,
    string Severity,
    string Package,
    string Version,
    string? FixedVersion,
    string Path,
    string? AdvisoryUrl);

public sealed record RepositoryOnboardingAssessment(
    int SchemaVersion,
    string AssessedAt,
    string RootPath,
    string TrustLevel,
    bool ReviewAllowed,
    string ReviewBoundary,
    RepositoryOnboardingCheck Secrets,
    RepositoryOnboardingCheck Dependencies,
    IReadOnlyList<RepositoryOnboardingSecretFinding> SecretFindings,
    IReadOnlyList<RepositoryDependencyAdvisory> Advisories);

public sealed class RepositoryOnboardingAssessmentService(RepositoryRegistry repositories, SensorRegistry sensors)
{
    private const int MaximumSurfacedFindings = 50;

    public async Task<RepositoryOnboardingAssessment> AssessAsync(
        RepositoryRegistrationRequest request,
        string? existingId,
        CancellationToken cancellationToken)
    {
        var assessmentRequest = existingId is null || request.TrustLevel is not null
            ? request
            : request with { TrustLevel = repositories.Get(existingId).TrustLevel };
        var candidate = repositories.Preview(assessmentRequest, existingId);
        var secretTask = RunSensorAsync(
            sensors.Get("gitleaks"),
            new SensorScanRequest(candidate.RootPath, PersistMetadata: false),
            cancellationToken);
        Task<SensorScanResult?> dependencyTask =
            string.Equals(candidate.TrustLevel, RepositoryTrustLevels.OperatorControlled, StringComparison.Ordinal)
                ? RunSensorAsync(
                    sensors.Get("dependencies"),
                    new SensorScanRequest(candidate.RootPath, PersistMetadata: false),
                    cancellationToken)
                : Task.FromResult<SensorScanResult?>(null);

        await Task.WhenAll(secretTask, dependencyTask).ConfigureAwait(false);
        var secretResult = await secretTask.ConfigureAwait(false);
        var dependencyResult = await dependencyTask.ConfigureAwait(false);
        var secretFindings = secretResult?.Findings
            .Take(MaximumSurfacedFindings)
            .Select(MapSecretFinding)
            .ToArray() ?? [];
        var advisories = dependencyResult?.Findings
            .Take(MaximumSurfacedFindings)
            .Select(MapAdvisory)
            .ToArray() ?? [];
        var secrets = SecretCheck(secretResult);
        var dependencies = DependencyCheck(candidate.TrustLevel, dependencyResult);
        var reviewAllowed =
            string.Equals(candidate.TrustLevel, RepositoryTrustLevels.OperatorControlled, StringComparison.Ordinal) &&
            string.Equals(secrets.Status, RepositoryOnboardingStatuses.Pass, StringComparison.Ordinal);
        var boundary = reviewAllowed
            ? "Operator-controlled content passed the mandatory secret scan. Model review may run with the existing local trust restriction."
            : string.Equals(candidate.TrustLevel, RepositoryTrustLevels.Untrusted, StringComparison.Ordinal)
                ? "Untrusted content is quarantined from model review and executable dependency sensors until isolated workers are available."
                : secrets.Status == RepositoryOnboardingStatuses.Block
                    ? "Active secret findings quarantine this repository from model review until they are removed, rotated, and rescanned."
                    : "The mandatory secret scan was unavailable, so model review remains quarantined.";

        return new RepositoryOnboardingAssessment(
            1,
            DateTimeOffset.UtcNow.ToString("O"),
            candidate.RootPath,
            candidate.TrustLevel,
            reviewAllowed,
            boundary,
            secrets,
            dependencies,
            secretFindings,
            advisories);
    }

    private static async Task<SensorScanResult?> RunSensorAsync(
        IReviewSensor sensor,
        SensorScanRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sensor.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SensorScanResult(
                false,
                $"{sensor.Id} could not complete: {exception.GetType().Name}",
                [],
                new SensorProvenance(sensor.Id, sensor.Version, "repository", ".",
                    DateTimeOffset.UtcNow.ToString("O"), new Dictionary<string, string>()));
        }
    }

    private static RepositoryOnboardingCheck SecretCheck(SensorScanResult? result)
    {
        if (result is null || !result.Available)
        {
            return new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Unavailable,
                false,
                0,
                "The redacted secret scan did not complete; review is quarantined.",
                result?.Provenance.ToolVersions ?? new Dictionary<string, string>());
        }

        return result.Findings.Count == 0
            ? new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Pass,
                true,
                0,
                "No active secret findings were detected.",
                result.Provenance.ToolVersions)
            : new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Block,
                true,
                result.Findings.Count,
                $"{result.Findings.Count} active secret finding(s) require removal and rotation.",
                result.Provenance.ToolVersions);
    }

    private static RepositoryOnboardingCheck DependencyCheck(string trustLevel, SensorScanResult? result)
    {
        if (string.Equals(trustLevel, RepositoryTrustLevels.Untrusted, StringComparison.Ordinal))
        {
            return new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Skipped,
                false,
                0,
                "Dependency commands were not executed against untrusted content without worker isolation.",
                new Dictionary<string, string>());
        }

        if (result is null || !result.Available)
        {
            return new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Unavailable,
                false,
                0,
                "Dependency advisory data was unavailable; onboarding may continue with this visible gap.",
                result?.Provenance.ToolVersions ?? new Dictionary<string, string>());
        }

        return result.Findings.Count == 0
            ? new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Pass,
                true,
                0,
                "No known vulnerable dependencies were reported.",
                result.Provenance.ToolVersions)
            : new RepositoryOnboardingCheck(
                RepositoryOnboardingStatuses.Warn,
                true,
                result.Findings.Count,
                $"{result.Findings.Count} dependency advisory finding(s) are surfaced for remediation.",
                result.Provenance.ToolVersions);
    }

    private static RepositoryOnboardingSecretFinding MapSecretFinding(ReviewFinding finding)
    {
        var location = finding.Locations.FirstOrDefault();
        return new RepositoryOnboardingSecretFinding(
            finding.RuleId,
            finding.Severity.ToString().ToLowerInvariant(),
            finding.Title,
            location?.Path ?? ".",
            location?.Range?.Start.Line);
    }

    private static RepositoryDependencyAdvisory MapAdvisory(ReviewFinding finding)
    {
        string package = "not reported";
        string version = "not reported";
        string? fixedVersion = null;
        string? advisoryUrl = null;
        if (!string.IsNullOrWhiteSpace(finding.Evidence))
        {
            try
            {
                using var document = JsonDocument.Parse(finding.Evidence);
                var root = document.RootElement;
                package = ReadString(root, "package") ?? package;
                version = ReadString(root, "version") ?? version;
                fixedVersion = ReadString(root, "fixedVersion");
                advisoryUrl = ReadString(root, "advisoryUrl");
            }
            catch (JsonException)
            {
                // The normalized finding still carries a stable advisory ID and path.
            }
        }

        return new RepositoryDependencyAdvisory(
            finding.RuleId,
            finding.Severity.ToString().ToLowerInvariant(),
            package,
            version,
            fixedVersion,
            finding.Locations.FirstOrDefault()?.Path ?? ".",
            advisoryUrl);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
