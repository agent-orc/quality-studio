using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class DeterministicEvidenceProjectionTests
{
    [Theory]
    [InlineData(FindingSeverity.Critical, DeterministicDisposition.Block)]
    [InlineData(FindingSeverity.High, DeterministicDisposition.Block)]
    [InlineData(FindingSeverity.Medium, DeterministicDisposition.Warn)]
    [InlineData(FindingSeverity.Low, DeterministicDisposition.Pass)]
    [InlineData(FindingSeverity.Info, DeterministicDisposition.Pass)]
    public void ClassifySensor_MapsSeverityToDisposition(FindingSeverity severity, DeterministicDisposition expected)
    {
        var result = Available("roslyn", [Finding(severity, "CA1822")]);

        Assert.Equal(expected, DeterministicEvidenceProjection.ClassifySensor(result));
    }

    [Fact]
    public void ClassifySensor_UnavailableSensorIsNeverPassOrWarn()
    {
        var result = new SensorScanResult(false, "dotnet build timed out", [], Provenance("roslyn"));

        Assert.Equal(DeterministicDisposition.Unavailable, DeterministicEvidenceProjection.ClassifySensor(result));
    }

    [Fact]
    public void Classify_TakesWorstAcrossSensors()
    {
        var clean = Available("tsc", []);
        var warn = Available("eslint", [Finding(FindingSeverity.Medium, "no-unused-vars")]);
        var blocking = Available("roslyn", [Finding(FindingSeverity.High, "CS0103")]);

        Assert.Equal(DeterministicDisposition.Block,
            DeterministicEvidenceProjection.Classify([clean, warn, blocking]));
        Assert.Equal(DeterministicDisposition.Warn,
            DeterministicEvidenceProjection.Classify([clean, warn]));
        Assert.Equal(DeterministicDisposition.Pass,
            DeterministicEvidenceProjection.Classify([clean]));
    }

    [Fact]
    public void Classify_UnavailableSensorOutranksAWarningFromAnotherSensor()
    {
        var warn = Available("eslint", [Finding(FindingSeverity.Medium, "no-unused-vars")]);
        var unavailable = new SensorScanResult(false, "npx eslint was not found", [], Provenance("eslint"));

        Assert.Equal(DeterministicDisposition.Unavailable,
            DeterministicEvidenceProjection.Classify([warn, unavailable]));
    }

    [Fact]
    public void Classify_EmptyEvidenceIsPass()
    {
        Assert.Equal(DeterministicDisposition.Pass, DeterministicEvidenceProjection.Classify([]));
    }

    [Fact]
    public void ToPromptJson_OmitsTitleDescriptionRecommendationAndEvidenceText()
    {
        var finding = new ReviewFinding(
            "roslyn-ca1822-aaaaaaaaaaaa",
            "analyzer",
            FindingSeverity.High,
            "Mark members as static",
            "Member does not access instance data and should be marked static for a small performance win.",
            "Make the member static.",
            [new FindingLocation("src/Small.cs", new FindingRange(
                new FindingPosition(7, 1), new FindingPosition(7, 5)))],
            "sha256:" + new string('a', 64),
            "CA1822",
            Evidence: "raw compiler output line 7");
        var evidence = new[] { Available("roslyn", [finding]) };

        var json = DeterministicEvidenceProjection.ToPromptJson(evidence);

        Assert.Contains("\"CA1822\"", json, StringComparison.Ordinal);
        Assert.Contains("\"high\"", json, StringComparison.Ordinal);
        Assert.Contains("\"src/Small.cs\"", json, StringComparison.Ordinal);
        Assert.Contains("\"block\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark members as static", json, StringComparison.Ordinal);
        Assert.DoesNotContain("performance win", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Make the member static", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw compiler output", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPromptJson_ReportsAggregateDispositionAndSeverityCounts()
    {
        var evidence = new[]
        {
            Available("roslyn", [Finding(FindingSeverity.Medium, "CA1822"), Finding(FindingSeverity.Medium, "CA1826")]),
        };

        using var document = JsonDocument.Parse(DeterministicEvidenceProjection.ToPromptJson(evidence));
        var root = document.RootElement;

        Assert.Equal("warn", root.GetProperty("disposition").GetString());
        var sensor = Assert.Single(root.GetProperty("sensors").EnumerateArray());
        Assert.Equal("warn", sensor.GetProperty("disposition").GetString());
        Assert.Equal(2, sensor.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, sensor.GetProperty("severityCounts").GetProperty("medium").GetInt32());
        Assert.Equal(0, sensor.GetProperty("severityCounts").GetProperty("high").GetInt32());
    }

    private static ReviewFinding Finding(FindingSeverity severity, string ruleId) => new(
        $"roslyn-{ruleId.ToLowerInvariant()}-{Guid.NewGuid():N}",
        "analyzer",
        severity,
        "Title",
        "Description",
        "Recommendation",
        [new FindingLocation("src/Small.cs", new FindingRange(
            new FindingPosition(1, 1), new FindingPosition(1, 5)))],
        "sha256:" + new string('a', 64),
        ruleId);

    private static SensorScanResult Available(string sensorId, IReadOnlyList<ReviewFinding> findings) =>
        new(true, null, findings, Provenance(sensorId));

    private static SensorProvenance Provenance(string sensorId) => new(
        sensorId, "1.0.0", "repository", ".", "2026-08-27T10:00:00.000Z",
        new Dictionary<string, string>(StringComparer.Ordinal));
}
