using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public enum SecurityEvidenceVerdict
{
    Pass,
    Warn,
    Block,
    Unavailable,
}

public sealed record SecuritySensorEvidence(
    string SensorId,
    string SensorVersion,
    string ResultHash,
    bool Available,
    string? UnavailableReason,
    SecurityEvidenceVerdict Verdict,
    IReadOnlyDictionary<string, string> ToolVersions,
    IReadOnlyList<ReviewFinding> Findings,
    bool Required = true);

public sealed record SecurityEvidenceBundle(
    SecurityEvidenceVerdict Verdict,
    IReadOnlyList<SecuritySensorEvidence> Sensors)
{
    public static SecurityEvidenceBundle Empty { get; } =
        new(SecurityEvidenceVerdict.Pass, Array.Empty<SecuritySensorEvidence>());

    public string ToPromptJson(int characterLimit = PreflightProjection.PromptCharacterLimit)
    {
        if (characterLimit < 256) throw new ArgumentOutOfRangeException(nameof(characterLimit));
        var root = new JsonObject
        {
            ["verdict"] = VerdictName(Verdict),
            ["sensors"] = new JsonArray(Sensors.Select(sensor => (JsonNode)PromptSensorJson(sensor)).ToArray()),
        };
        var indented = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(indented);
        if (json.Length <= characterLimit) return json;
        return new JsonObject
        {
            ["verdict"] = VerdictName(Verdict),
            ["truncated"] = true,
            ["sensors"] = new JsonArray(Sensors.Select(sensor => (JsonNode)new JsonObject
            {
                ["id"] = sensor.SensorId,
                ["resultHash"] = sensor.ResultHash,
                ["verdict"] = VerdictName(sensor.Verdict),
                ["findingCount"] = sensor.Findings.Count,
                ["ruleIds"] = new JsonArray(sensor.Findings.Select(finding => finding.RuleId)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(rule => (JsonNode)rule).ToArray()),
            }).ToArray()),
        }.ToJsonString(indented);
    }

    private static JsonObject PromptSensorJson(SecuritySensorEvidence sensor)
    {
        var value = new JsonObject
        {
            ["id"] = sensor.SensorId,
            ["resultHash"] = sensor.ResultHash,
            ["available"] = sensor.Available,
            ["required"] = sensor.Required,
            ["verdict"] = VerdictName(sensor.Verdict),
            ["findingCount"] = sensor.Findings.Count,
        };
        if (string.Equals(sensor.SensorId, "gitleaks", StringComparison.OrdinalIgnoreCase)) return value;
        value["findings"] = new JsonArray(sensor.Findings.Select(finding => (JsonNode)new JsonObject
        {
            ["ruleId"] = finding.RuleId,
            ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
            ["fingerprint"] = finding.Fingerprint,
            ["paths"] = new JsonArray(finding.Locations.Select(location => NormalizePath(location.Path))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
        }).ToArray());
        return value;
    }

    internal static string VerdictName(SecurityEvidenceVerdict verdict) =>
        verdict.ToString().ToLowerInvariant();

    internal static JsonObject SensorJson(SecuritySensorEvidence sensor, bool includeFindings)
    {
        var value = new JsonObject
        {
            ["id"] = sensor.SensorId,
            ["version"] = sensor.SensorVersion,
            ["resultHash"] = sensor.ResultHash,
            ["available"] = sensor.Available,
            ["unavailableReason"] = sensor.UnavailableReason,
            ["required"] = sensor.Required,
            ["verdict"] = VerdictName(sensor.Verdict),
            ["toolVersions"] = new JsonObject(sensor.ToolVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
        };
        if (includeFindings)
        {
            value["findings"] = new JsonArray(sensor.Findings.Select(finding => (JsonNode)FindingJson(finding, sensor)).ToArray());
        }
        return value;
    }

    internal static JsonObject FindingJson(ReviewFinding finding, SecuritySensorEvidence sensor)
    {
        var originalEvidence = ParseEvidence(finding.Evidence);
        return new JsonObject
        {
            ["id"] = finding.Id,
            ["aspect"] = finding.Aspect,
            ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
            ["title"] = finding.Title,
            ["description"] = finding.Description,
            ["recommendation"] = finding.Recommendation,
            ["locations"] = new JsonArray(finding.Locations.Select(location => (JsonNode)LocationJson(location)).ToArray()),
            ["fingerprint"] = finding.Fingerprint,
            ["ruleId"] = finding.RuleId,
            ["evidence"] = new JsonObject
            {
                ["source"] = "machine-sensor",
                ["sensorId"] = sensor.SensorId,
                ["sensorVersion"] = sensor.SensorVersion,
                ["resultHash"] = sensor.ResultHash,
                ["fact"] = originalEvidence,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        };
    }

    private static JsonObject LocationJson(FindingLocation location)
    {
        var value = new JsonObject { ["path"] = NormalizePath(location.Path) };
        if (location.Range is not null)
        {
            value["range"] = new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["line"] = location.Range.Start.Line,
                    ["column"] = location.Range.Start.Column,
                },
                ["end"] = new JsonObject
                {
                    ["line"] = location.Range.End.Line,
                    ["column"] = location.Range.End.Column,
                },
            };
        }
        if (location.SymbolId is not null) value["symbolId"] = location.SymbolId;
        return value;
    }

    private static JsonNode? ParseEvidence(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        try
        {
            return JsonNode.Parse(evidence);
        }
        catch (JsonException)
        {
            return evidence;
        }
    }

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}

public sealed class SecurityEvidenceCollector(SensorRegistry registry)
{
    public async Task<SecurityEvidenceBundle> CollectAsync(
        string repositoryRoot,
        IReadOnlyList<string> subjectPaths,
        IReadOnlyList<ReviewSensorConfiguration> configurations,
        CancellationToken cancellationToken = default)
    {
        var subjects = subjectPaths.Select(SecurityEvidenceBundle.NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        var tasks = configurations
            .DistinctBy(configuration => configuration.Id, StringComparer.OrdinalIgnoreCase)
            .Select(configuration => CollectSensorAsync(repositoryRoot, subjects, configuration, cancellationToken))
            .ToArray();
        var evidence = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Bundle(evidence);
    }

    public static SecurityEvidenceBundle FromPreflight(
        IReadOnlyList<PreflightResult> results,
        IReadOnlyList<string> subjectPaths,
        IReadOnlyList<ReviewSensorConfiguration> configurations)
    {
        var subjects = subjectPaths.Select(SecurityEvidenceBundle.NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        var byId = results.ToDictionary(result => result.Check.Id, StringComparer.OrdinalIgnoreCase);
        var sensors = configurations
            .DistinctBy(configuration => configuration.Id, StringComparer.OrdinalIgnoreCase)
            .Select(configuration => byId.TryGetValue(configuration.Id, out var result)
                ? Project(result, subjects)
                : Unavailable(configuration.Id, "unknown", "Preflight result was not collected.", configuration.Required))
            .ToArray();
        return Bundle(sensors);
    }

    private static SecurityEvidenceBundle Bundle(IEnumerable<SecuritySensorEvidence> evidence)
    {
        var ordered = evidence.OrderBy(sensor => sensor.SensorId, StringComparer.Ordinal).ToArray();
        var verdict = ordered.Any(sensor => sensor.Required && sensor.Verdict == SecurityEvidenceVerdict.Unavailable)
            ? SecurityEvidenceVerdict.Unavailable
            : ordered.Any(sensor => sensor.Verdict == SecurityEvidenceVerdict.Block)
                ? SecurityEvidenceVerdict.Block
                : ordered.Any(sensor => sensor.Verdict == SecurityEvidenceVerdict.Warn)
                    ? SecurityEvidenceVerdict.Warn
                    : SecurityEvidenceVerdict.Pass;
        return new SecurityEvidenceBundle(verdict, ordered);
    }

    private static SecuritySensorEvidence Project(
        PreflightResult result,
        IReadOnlySet<string> subjectPaths)
    {
        var findings = result.Findings
            .Select(finding => finding with
            {
                Locations = finding.Locations.Where(location =>
                    subjectPaths.Contains(SecurityEvidenceBundle.NormalizePath(location.Path))).ToArray(),
            })
            .Where(finding => finding.Locations.Count > 0)
            .OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var available = result.Status is not (PreflightStatus.Unavailable or PreflightStatus.Failed);
        var verdict = !available
            ? SecurityEvidenceVerdict.Unavailable
            : findings.Any(finding => finding.Severity is FindingSeverity.Critical or FindingSeverity.High)
                ? SecurityEvidenceVerdict.Block
                : findings.Length > 0
                    ? SecurityEvidenceVerdict.Warn
                    : SecurityEvidenceVerdict.Pass;
        return new SecuritySensorEvidence(
            result.Check.Id,
            result.Check.Version,
            result.ResultHash,
            available,
            result.StatusReason,
            verdict,
            result.Check.ToolVersions,
            findings,
            result.Check.Required);
    }

    private async Task<SecuritySensorEvidence> CollectSensorAsync(
        string repositoryRoot,
        IReadOnlySet<string> subjectPaths,
        ReviewSensorConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IReviewSensor sensor;
        try
        {
            sensor = registry.Get(configuration.Id);
        }
        catch (SensorNotFoundException exception)
        {
            return Unavailable(configuration.Id, "unknown", exception.Message);
        }

        try
        {
            var result = await sensor.RunAsync(new SensorScanRequest(
                repositoryRoot,
                SensorScope.Repository,
                Configuration: configuration.Configuration,
                PersistMetadata: false), cancellationToken).ConfigureAwait(false);
            var findings = result.Findings
                .Select(finding => finding with
                {
                    Locations = finding.Locations.Where(location =>
                        subjectPaths.Contains(SecurityEvidenceBundle.NormalizePath(location.Path))).ToArray(),
                })
                .Where(finding => finding.Locations.Count > 0)
                .OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .ToArray();
            var verdict = !result.Available
                ? SecurityEvidenceVerdict.Unavailable
                : findings.Any(finding => finding.Severity is FindingSeverity.Critical or FindingSeverity.High)
                    ? SecurityEvidenceVerdict.Block
                    : findings.Length > 0
                        ? SecurityEvidenceVerdict.Warn
                        : SecurityEvidenceVerdict.Pass;
            var draft = new SecuritySensorEvidence(
                result.Provenance.SensorId,
                result.Provenance.SensorVersion,
                string.Empty,
                result.Available,
                result.UnavailableReason,
                verdict,
                result.Provenance.ToolVersions,
                findings,
                true);
            return draft with { ResultHash = Hash(draft) };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Unavailable(sensor.Id, sensor.Version, $"Sensor execution failed: {exception.Message}");
        }
    }

    private static SecuritySensorEvidence Unavailable(string id, string version, string reason, bool required = true)
    {
        var draft = new SecuritySensorEvidence(
            id,
            version,
            string.Empty,
            false,
            reason,
            SecurityEvidenceVerdict.Unavailable,
            new Dictionary<string, string>(),
            Array.Empty<ReviewFinding>(),
            required);
        return draft with { ResultHash = Hash(draft) };
    }

    private static string Hash(SecuritySensorEvidence evidence)
    {
        var canonical = new JsonObject
        {
            ["id"] = evidence.SensorId,
            ["version"] = evidence.SensorVersion,
            ["available"] = evidence.Available,
            ["unavailableReason"] = evidence.UnavailableReason,
            ["required"] = evidence.Required,
            ["verdict"] = SecurityEvidenceBundle.VerdictName(evidence.Verdict),
            ["toolVersions"] = new JsonObject(evidence.ToolVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
            ["findings"] = new JsonArray(evidence.Findings.Select(finding => (JsonNode)CanonicalFinding(finding)).ToArray()),
        }.ToJsonString();
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonObject CanonicalFinding(ReviewFinding finding) => new()
    {
        ["id"] = finding.Id,
        ["aspect"] = finding.Aspect,
        ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
        ["title"] = finding.Title,
        ["description"] = finding.Description,
        ["recommendation"] = finding.Recommendation,
        ["locations"] = new JsonArray(finding.Locations.Select(location => (JsonNode)new JsonObject
        {
            ["path"] = SecurityEvidenceBundle.NormalizePath(location.Path),
            ["range"] = location.Range is null ? null : new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["line"] = location.Range.Start.Line,
                    ["column"] = location.Range.Start.Column,
                },
                ["end"] = new JsonObject
                {
                    ["line"] = location.Range.End.Line,
                    ["column"] = location.Range.End.Column,
                },
            },
            ["symbolId"] = location.SymbolId,
        }).ToArray()),
        ["fingerprint"] = finding.Fingerprint,
        ["ruleId"] = finding.RuleId,
        ["evidence"] = ParseEvidence(finding.Evidence),
    };

    private static JsonNode? ParseEvidence(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        try
        {
            return JsonNode.Parse(evidence);
        }
        catch (JsonException)
        {
            return evidence;
        }
    }
}

public static class SecurityReviewCombiner
{
    private static readonly (string Id, string Title)[] ProjectAspects =
    [
        ("secrets", "Secrets"),
        ("dependencies", "Dependencies"),
        ("authentication-authorization", "Authentication / authorization"),
        ("input-validation", "Input validation"),
        ("configuration-iac", "Configuration and IaC"),
    ];

    public static void PrepareAgentResponse(
        JsonObject response,
        SecurityEvidenceBundle evidence,
        ReviewLevel level)
    {
        if (evidence.Sensors.Count == 0) return;
        var findings = response["findings"]!.AsArray();
        foreach (var agentFinding in findings.OfType<JsonObject>().ToArray())
        {
            if (evidence.Sensors.SelectMany(sensor => sensor.Findings)
                .Any(sensorFinding => Duplicates(agentFinding, sensorFinding)))
            {
                findings.Remove(agentFinding);
            }
        }

        var aspects = response["aspects"]!.AsArray();
        if (level == ReviewLevel.Project)
        {
            foreach (var expected in ProjectAspects) EnsureAspect(aspects, expected.Id, expected.Title, response["grade"]!.AsObject());
        }
        foreach (var sensorFinding in evidence.Sensors.SelectMany(sensor => sensor.Findings))
        {
            EnsureAspect(aspects, sensorFinding.Aspect, AspectTitle(sensorFinding.Aspect), response["grade"]!.AsObject());
        }
        if (evidence.Verdict == SecurityEvidenceVerdict.Unavailable)
        {
            EnsureAspect(aspects, "sensor-availability", "Sensor availability", response["grade"]!.AsObject());
        }

        ApplyCombinationGrade(response, evidence);
    }

    public static IReadOnlyList<FindingIdentityRecord> AppendSensorFindings(
        JsonObject response,
        SecurityEvidenceBundle evidence)
    {
        var identities = new List<FindingIdentityRecord>();
        var findings = response["findings"]!.AsArray();
        foreach (var sensor in evidence.Sensors)
        {
            foreach (var finding in sensor.Findings)
            {
                findings.Add(SecurityEvidenceBundle.FindingJson(finding, sensor));
                var path = SecurityEvidenceBundle.NormalizePath(finding.Locations.FirstOrDefault()?.Path ?? ".");
                identities.Add(new FindingIdentityRecord(finding.Fingerprint, finding.Id, path, finding.RuleId));
            }
        }
        return identities;
    }

    public static JsonObject Metadata(SecurityEvidenceBundle evidence) => new()
    {
        ["verdict"] = SecurityEvidenceBundle.VerdictName(evidence.Verdict),
        ["combinationRule"] = "security-sensor-agent-v1",
        ["sensors"] = new JsonArray(evidence.Sensors.Select(sensor =>
            (JsonNode)SecurityEvidenceBundle.SensorJson(sensor, includeFindings: false)).ToArray()),
    };

    private static void ApplyCombinationGrade(JsonObject response, SecurityEvidenceBundle evidence)
    {
        var maximum = evidence.Verdict switch
        {
            SecurityEvidenceVerdict.Block => 59,
            SecurityEvidenceVerdict.Warn => 79,
            SecurityEvidenceVerdict.Unavailable => 59,
            _ => 100,
        };
        var grade = response["grade"]!.AsObject();
        var agentScore = grade["score"]!.GetValue<int>();
        var score = Math.Min(agentScore, maximum);
        grade["score"] = score;
        grade["band"] = Band(score);
        var machineStatement = evidence.Verdict switch
        {
            SecurityEvidenceVerdict.Block => "Machine sensors reported blocking security evidence.",
            SecurityEvidenceVerdict.Warn => "Machine sensors reported warning-level security evidence.",
            SecurityEvidenceVerdict.Unavailable => "At least one required machine sensor was unavailable; this review is not a clean result.",
            _ when evidence.Sensors.Any(sensor => !sensor.Available) =>
                "At least one optional machine sensor was unavailable; deterministic coverage is incomplete.",
            _ => "Machine sensors completed without active findings.",
        };
        grade["rationale"] = $"{machineStatement} Agent judgement: {grade["rationale"]!.GetValue<string>()}";
        response["summary"] = $"{machineStatement} {response["summary"]!.GetValue<string>()}";

        foreach (var aspect in response["aspects"]!.AsArray().OfType<JsonObject>())
        {
            var aspectId = aspect["id"]!.GetValue<string>();
            var affected = evidence.Verdict == SecurityEvidenceVerdict.Unavailable
                ? aspectId == "sensor-availability"
                : evidence.Sensors.SelectMany(sensor => sensor.Findings).Any(finding => finding.Aspect == aspectId);
            if (!affected) continue;
            var aspectGrade = aspect["grade"]!.AsObject();
            var aspectScore = Math.Min(aspectGrade["score"]!.GetValue<int>(), maximum);
            aspectGrade["score"] = aspectScore;
            aspectGrade["band"] = Band(aspectScore);
            aspectGrade["rationale"] = $"{machineStatement} {aspectGrade["rationale"]!.GetValue<string>()}";
        }
    }

    private static bool Duplicates(JsonObject agentFinding, ReviewFinding sensorFinding)
    {
        if (string.Equals(agentFinding["ruleId"]?.GetValue<string>(), sensorFinding.RuleId, StringComparison.Ordinal))
            return true;
        foreach (var agentLocation in agentFinding["locations"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var agentPath = SecurityEvidenceBundle.NormalizePath(agentLocation["path"]!.GetValue<string>());
            foreach (var sensorLocation in sensorFinding.Locations)
            {
                if (!string.Equals(agentPath, SecurityEvidenceBundle.NormalizePath(sensorLocation.Path), StringComparison.Ordinal))
                    continue;
                if (RangesOverlap(agentLocation["range"] as JsonObject, sensorLocation.Range)) return true;
            }
        }
        return false;
    }

    private static bool RangesOverlap(JsonObject? agentRange, FindingRange? sensorRange)
    {
        if (agentRange is null || sensorRange is null) return false;
        var start = agentRange["start"]!.AsObject()["line"]!.GetValue<int>();
        var end = agentRange["end"]!.AsObject()["line"]!.GetValue<int>();
        return start <= sensorRange.End.Line && sensorRange.Start.Line <= end;
    }

    private static void EnsureAspect(JsonArray aspects, string id, string title, JsonObject fallbackGrade)
    {
        if (aspects.OfType<JsonObject>().Any(aspect => aspect["id"]?.GetValue<string>() == id)) return;
        aspects.Add(new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["grade"] = fallbackGrade.DeepClone(),
        });
    }

    private static string AspectTitle(string id) => id switch
    {
        "secrets" => "Secrets",
        "dependencies" => "Dependencies",
        _ => string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
    };

    private static string Band(int score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F",
    };
}
