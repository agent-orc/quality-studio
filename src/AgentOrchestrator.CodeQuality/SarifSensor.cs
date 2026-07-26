using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Runs an optional repository-owned command and ingests its SARIF 2.1.0 report.</summary>
public sealed class SarifSensor : IDeterministicEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner commandRunner;
    private readonly string id;

    public SarifSensor(ISensorCommandRunner? commandRunner = null) : this("sarif", commandRunner)
    {
    }

    internal SarifSensor(string id, ISensorCommandRunner? commandRunner)
    {
        this.id = id;
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();
    }

    public string Id => id;
    public string Version => SensorVersion;
    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            ["sarif"] = "2.1.0",
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        var configuration = request.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!configuration.TryGetValue("reportPath", out var configuredReport) ||
            string.IsNullOrWhiteSpace(configuredReport))
        {
            return Unavailable(request, "SARIF analyzer configuration requires reportPath.");
        }

        string reportPath;
        string workingDirectory;
        string target;
        try
        {
            target = request.Scope == SensorScope.Path && !string.IsNullOrWhiteSpace(request.Path)
                ? AnalyzerCommand.ContainedPath(root, request.Path)
                : root;
            reportPath = AnalyzerCommand.ContainedPath(root, configuredReport);
            workingDirectory = configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
                               !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? AnalyzerCommand.ContainedPath(root, configuredWorkingDirectory)
                : Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
            if (!Directory.Exists(workingDirectory))
                return Unavailable(request, "Analyzer workingDirectory must be an existing repository directory.");
        }
        catch (ArgumentException exception)
        {
            return Unavailable(request, exception.Message);
        }

        if (configuration.TryGetValue("command", out var configuredCommand) &&
            !string.IsNullOrWhiteSpace(configuredCommand))
        {
            IReadOnlyList<string> command;
            try
            {
                command = AnalyzerCommand.Expand(
                    configuredCommand, root, target, reportPath);
            }
            catch (ArgumentException exception)
            {
                return Unavailable(request, exception.Message);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                if (File.Exists(reportPath)) File.Delete(reportPath);
                var output = await commandRunner.RunAsync(
                    command[0], command.Skip(1).ToArray(), workingDirectory, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(reportPath))
                {
                    return Unavailable(request,
                        $"{command[0]} exited with code {output.ExitCode} without producing SARIF report " +
                        $"'{configuredReport}'. {AnalyzerCommand.OutputDetail(output)}");
                }
            }
            catch (Exception exception) when (
                exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
            {
                return Unavailable(request, $"{Id} is unavailable: {exception.Message}");
            }
        }
        else if (!File.Exists(reportPath))
        {
            return Unavailable(request, $"SARIF report is unavailable: '{configuredReport}' does not exist.");
        }

        try
        {
            await using var stream = File.OpenRead(reportPath);
            var findings = await ParseAsync(stream, root, Id, cancellationToken).ConfigureAwait(false);
            var versions = findings
                .Where(finding => finding.Source is not null)
                .GroupBy(finding => finding.Source!.Producer, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(finding => finding.Source!.ProducerVersion)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "not reported",
                    StringComparer.Ordinal);
            versions["sarif"] = "2.1.0";
            return new SensorScanResult(true, null, findings, Provenance(request, versions));
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or InvalidDataException or InvalidOperationException)
        {
            return Unavailable(request, $"SARIF report is unavailable: {exception.Message}");
        }
    }

    public static async Task<IReadOnlyList<ReviewFinding>> ParseAsync(
        Stream stream,
        string repositoryRoot,
        string sensorId = "sarif",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var root = Path.GetFullPath(repositoryRoot);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var sarif = document.RootElement;
        if (sarif.ValueKind != JsonValueKind.Object ||
            !sarif.TryGetProperty("version", out var version) ||
            version.GetString() != "2.1.0")
        {
            throw new InvalidDataException("Only SARIF 2.1.0 reports are supported.");
        }
        if (!sarif.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SARIF 2.1.0 report must contain a runs array.");

        var findings = new List<ReviewFinding>();
        var runIndex = 0;
        foreach (var run in runs.EnumerateArray())
        {
            var driver = RequireObject(RequireObject(run, "tool"), "driver");
            var producer = String(driver, "name");
            if (string.IsNullOrWhiteSpace(producer))
                throw new InvalidDataException($"SARIF run {runIndex} has no tool.driver.name.");
            var producerVersion = String(driver, "semanticVersion") ??
                                  String(driver, "version") ??
                                  String(driver, "dottedQuadFileVersion");
            var context = new RunContext(
                root,
                ReadRuleComponents(run, driver),
                ReadArtifacts(run),
                ReadUriBases(run));
            if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                var resultIndex = 0;
                foreach (var result in results.EnumerateArray())
                {
                    if (String(result, "kind") is not ("pass" or "notApplicable"))
                    {
                        findings.Add(MapResult(
                            result, context, sensorId, producer, producerVersion, runIndex, resultIndex));
                    }
                    resultIndex++;
                }
            }
            runIndex++;
        }

        return findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations[0].Range?.Start.Line ?? 0)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ReviewFinding MapResult(
        JsonElement result,
        RunContext context,
        string sensorId,
        string producer,
        string? producerVersion,
        int runIndex,
        int resultIndex)
    {
        var rule = ResolveRule(result, context.Rules);
        var ruleId = String(result, "ruleId") ??
                     (result.TryGetProperty("rule", out var reference) ? String(reference, "id") : null) ??
                     rule?.Id ??
                     "sarif:unclassified";
        var message = ResolveMessage(result, rule) ?? $"{producer} reported {ruleId}.";
        var locations = ReadLocations(result, context);
        if (locations.Count == 0) locations = [new FindingLocation(".")];
        var title = rule?.ShortDescription ?? FirstLine(message);
        var recommendation = rule?.Help ?? rule?.FullDescription ??
                             $"Review analyzer rule {ruleId} and address it when applicable.";
        var reportedFingerprint = Fingerprint(result);
        var fingerprintMaterial = reportedFingerprint is null
            ? $"{producer}\0{ruleId}\0{message}\0{string.Join('\0', locations.Select(LocationKey))}"
            : $"{producer}\0{ruleId}\0{reportedFingerprint}";
        var fingerprint = "sha256:" + Sha256(fingerprintMaterial);
        var evidence = JsonSerializer.Serialize(new
        {
            sarifRun = runIndex,
            sarifResult = resultIndex,
            producer,
            producerVersion,
            rule = new
            {
                id = ruleId,
                name = rule?.Name,
                helpUri = rule?.HelpUri,
                properties = rule?.Properties,
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (evidence.Length > 10_000)
        {
            evidence = JsonSerializer.Serialize(new
            {
                sarifRun = runIndex,
                sarifResult = resultIndex,
                producer,
                producerVersion,
                rule = new { id = ruleId, name = rule?.Name, helpUri = rule?.HelpUri },
                metadataOmitted = "Rule properties exceeded the finding evidence size limit.",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        return new ReviewFinding(
            BuildId(producer, ruleId, fingerprint),
            "analyzer",
            Severity(String(result, "level") ?? rule?.DefaultLevel),
            Trim(title, 300),
            Trim(message, 10_000),
            Trim(recommendation, 10_000),
            locations,
            fingerprint,
            Trim(ruleId, 200),
            evidence,
            new FindingSource(
                FindingSourceKind.Deterministic, sensorId, producer, producerVersion, runIndex));
    }

    private static IReadOnlyList<IReadOnlyList<SarifRule>> ReadRuleComponents(
        JsonElement run,
        JsonElement driver)
    {
        var components = new List<IReadOnlyList<SarifRule>> { ReadRules(driver) };
        if (run.GetProperty("tool").TryGetProperty("extensions", out var extensions) &&
            extensions.ValueKind == JsonValueKind.Array)
        {
            components.AddRange(extensions.EnumerateArray().Select(ReadRules));
        }
        return components;
    }

    private static IReadOnlyList<SarifRule> ReadRules(JsonElement component)
    {
        if (!component.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return [];
        return rules.EnumerateArray().Select(rule => new SarifRule(
            String(rule, "id") ?? "sarif:unclassified",
            String(rule, "name"),
            MessageText(rule, "shortDescription"),
            MessageText(rule, "fullDescription"),
            MessageText(rule, "help"),
            String(rule, "helpUri"),
            rule.TryGetProperty("defaultConfiguration", out var configuration)
                ? String(configuration, "level")
                : null,
            ReadMessageStrings(rule),
            rule.TryGetProperty("properties", out var properties) ? properties.Clone() : null)).ToArray();
    }

    private static SarifRule? ResolveRule(
        JsonElement result,
        IReadOnlyList<IReadOnlyList<SarifRule>> components)
    {
        var componentIndex = 0;
        int? ruleIndex = null;
        if (result.TryGetProperty("rule", out var reference) && reference.ValueKind == JsonValueKind.Object)
        {
            ruleIndex = Int(reference, "index");
            if (reference.TryGetProperty("toolComponent", out var toolComponent))
                componentIndex = (Int(toolComponent, "index") ?? -1) + 1;
        }
        ruleIndex ??= Int(result, "ruleIndex");
        if (componentIndex >= 0 && componentIndex < components.Count &&
            ruleIndex is >= 0 && ruleIndex < components[componentIndex].Count)
        {
            return components[componentIndex][ruleIndex.Value];
        }
        var id = String(result, "ruleId") ??
                 (result.TryGetProperty("rule", out reference) ? String(reference, "id") : null);
        return id is null
            ? null
            : components.SelectMany(component => component)
                .FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, string> ReadMessageStrings(JsonElement rule)
    {
        if (!rule.TryGetProperty("messageStrings", out var messages) ||
            messages.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        return messages.EnumerateObject()
            .Select(property => KeyValuePair.Create(
                property.Name,
                String(property.Value, "text") ?? String(property.Value, "markdown")))
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }

    private static string? ResolveMessage(JsonElement result, SarifRule? rule)
    {
        if (!result.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        var text = String(message, "text") ?? String(message, "markdown");
        if (text is null && rule is not null && String(message, "id") is { } messageId)
            rule.MessageStrings.TryGetValue(messageId, out text);
        if (text is null) return null;
        if (!message.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Array)
            return text;
        var index = 0;
        foreach (var argument in arguments.EnumerateArray())
        {
            text = text.Replace(
                "{" + index.ToString(CultureInfo.InvariantCulture) + "}",
                argument.ValueKind == JsonValueKind.String ? argument.GetString()! : argument.GetRawText(),
                StringComparison.Ordinal);
            index++;
        }
        return text;
    }

    private static IReadOnlyList<FindingLocation> ReadLocations(JsonElement result, RunContext context)
    {
        var locations = new List<FindingLocation>();
        AddLocationArray(result, "locations", locations, context);
        AddLocationArray(result, "relatedLocations", locations, context);
        if (result.TryGetProperty("codeFlows", out var codeFlows) && codeFlows.ValueKind == JsonValueKind.Array)
        {
            foreach (var flow in codeFlows.EnumerateArray())
            {
                if (!flow.TryGetProperty("threadFlows", out var threadFlows) ||
                    threadFlows.ValueKind != JsonValueKind.Array) continue;
                foreach (var threadFlow in threadFlows.EnumerateArray())
                {
                    if (!threadFlow.TryGetProperty("locations", out var nested) ||
                        nested.ValueKind != JsonValueKind.Array) continue;
                    foreach (var wrapper in nested.EnumerateArray())
                    {
                        if (wrapper.TryGetProperty("location", out var location) &&
                            MapLocation(location, context) is { } mapped)
                            locations.Add(mapped);
                    }
                }
            }
        }
        if (result.TryGetProperty("stacks", out var stacks) && stacks.ValueKind == JsonValueKind.Array)
        {
            foreach (var stack in stacks.EnumerateArray())
            {
                if (!stack.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var frame in frames.EnumerateArray())
                {
                    if (frame.TryGetProperty("location", out var location) &&
                        MapLocation(location, context) is { } mapped)
                        locations.Add(mapped);
                }
            }
        }
        return locations.DistinctBy(LocationKey, StringComparer.Ordinal).ToArray();
    }

    private static void AddLocationArray(
        JsonElement parent,
        string property,
        ICollection<FindingLocation> locations,
        RunContext context)
    {
        if (!parent.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
            if (MapLocation(value, context) is { } mapped) locations.Add(mapped);
    }

    private static FindingLocation? MapLocation(JsonElement location, RunContext context)
    {
        if (!location.TryGetProperty("physicalLocation", out var physical) ||
            physical.ValueKind != JsonValueKind.Object) return null;
        if (!physical.TryGetProperty("artifactLocation", out var artifactLocation) ||
            artifactLocation.ValueKind != JsonValueKind.Object) return null;
        var artifact = ResolveArtifact(artifactLocation, context.Artifacts);
        var path = ResolvePath(artifact.Uri, artifact.BaseId, context);
        FindingRange? range = null;
        if (physical.TryGetProperty("region", out var region) && region.ValueKind == JsonValueKind.Object &&
            Int(region, "startLine") is { } startLine)
        {
            var startColumn = Int(region, "startColumn") ?? 1;
            var endLine = Math.Max(startLine, Int(region, "endLine") ?? startLine);
            var endColumn = Int(region, "endColumn") ?? startColumn;
            range = new FindingRange(
                new FindingPosition(Math.Max(1, startLine), Math.Max(1, startColumn)),
                new FindingPosition(Math.Max(1, endLine), Math.Max(1, endColumn)));
        }
        string? symbol = null;
        if (location.TryGetProperty("logicalLocations", out var logicalLocations) &&
            logicalLocations.ValueKind == JsonValueKind.Array)
        {
            var logical = logicalLocations.EnumerateArray().FirstOrDefault();
            if (logical.ValueKind == JsonValueKind.Object)
                symbol = String(logical, "fullyQualifiedName") ??
                         String(logical, "decoratedName") ??
                         String(logical, "name");
        }
        return new FindingLocation(path, range, symbol);
    }

    private static Artifact ResolveArtifact(
        JsonElement location,
        IReadOnlyList<Artifact> artifacts)
    {
        var uri = String(location, "uri");
        var baseId = String(location, "uriBaseId");
        if (Int(location, "index") is { } index && index >= 0 && index < artifacts.Count)
        {
            uri ??= artifacts[index].Uri;
            baseId ??= artifacts[index].BaseId;
        }
        return new Artifact(uri ?? ".", baseId);
    }

    private static IReadOnlyList<Artifact> ReadArtifacts(JsonElement run)
    {
        if (!run.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            return [];
        return artifacts.EnumerateArray().Select(artifact =>
        {
            if (!artifact.TryGetProperty("location", out var location))
                return new Artifact(".", null);
            return new Artifact(String(location, "uri") ?? ".", String(location, "uriBaseId"));
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, Artifact> ReadUriBases(JsonElement run)
    {
        if (!run.TryGetProperty("originalUriBaseIds", out var bases) || bases.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, Artifact>(StringComparer.Ordinal);
        return bases.EnumerateObject().ToDictionary(
            property => property.Name,
            property => new Artifact(
                String(property.Value, "uri") ?? string.Empty,
                String(property.Value, "uriBaseId")),
            StringComparer.Ordinal);
    }

    private static string ResolvePath(string uriValue, string? baseId, RunContext context)
    {
        var value = Uri.UnescapeDataString(uriValue.Replace('\\', '/'));
        if (baseId is not null && context.UriBases.TryGetValue(baseId, out var baseArtifact))
        {
            var resolvedBase = ResolveBase(baseArtifact, context.UriBases, new HashSet<string>(StringComparer.Ordinal));
            if (Uri.TryCreate(resolvedBase, UriKind.Absolute, out var baseUri))
                value = new Uri(baseUri, value).IsFile ? new Uri(baseUri, value).LocalPath : new Uri(baseUri, value).ToString();
            else
                value = Path.Combine(resolvedBase, value);
        }

        string absolute;
        if (Path.IsPathRooted(value))
        {
            absolute = value;
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return ExternalPath(uri);
            absolute = uri.LocalPath;
        }
        else
        {
            absolute = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(context.Root, value));
        }
        if (AnalyzerCommand.IsWithin(context.Root, absolute))
            return Path.GetRelativePath(context.Root, absolute).Replace('\\', '/');
        return "external/" + Path.GetFileName(absolute);
    }

    private static string ResolveBase(
        Artifact artifact,
        IReadOnlyDictionary<string, Artifact> bases,
        ISet<string> visited)
    {
        if (artifact.BaseId is null || !bases.TryGetValue(artifact.BaseId, out var parent) ||
            !visited.Add(artifact.BaseId))
            return artifact.Uri;
        var parentValue = ResolveBase(parent, bases, visited);
        if (Uri.TryCreate(parentValue, UriKind.Absolute, out var parentUri))
            return new Uri(parentUri, artifact.Uri).ToString();
        return Path.Combine(parentValue, artifact.Uri);
    }

    private static string ExternalPath(Uri uri)
    {
        var pieces = new[] { uri.Host }
            .Concat(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(piece => piece.Length > 0)
            .Select(piece => piece is "." or ".."
                ? piece.Replace('.', '_')
                : piece.Replace('\\', '_').Replace('/', '_'));
        var path = string.Join('/', pieces);
        return "external/" + (path.Length == 0 ? "location" : path);
    }

    private static string? Fingerprint(JsonElement result)
    {
        foreach (var propertyName in new[] { "partialFingerprints", "fingerprints" })
        {
            if (!result.TryGetProperty(propertyName, out var values) ||
                values.ValueKind != JsonValueKind.Object) continue;
            var parts = values.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value}")
                .ToArray();
            if (parts.Length > 0) return string.Join('\0', parts);
        }
        return null;
    }

    private SensorScanResult Unavailable(SensorScanRequest request, string reason) =>
        new(false, reason, [], Provenance(request, new Dictionary<string, string>
        {
            ["sarif"] = "2.1.0",
        }));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, request.Scope.ToString().ToLowerInvariant(),
            request.Scope == SensorScope.Repository ? "." : request.Path ?? "(missing)",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    private static FindingSeverity Severity(string? level) => level?.ToLowerInvariant() switch
    {
        "error" => FindingSeverity.High,
        "warning" => FindingSeverity.Medium,
        "note" => FindingSeverity.Info,
        _ => FindingSeverity.Info,
    };

    private static string BuildId(string producer, string ruleId, string fingerprint)
    {
        var slug = new string($"{producer}-{ruleId}".ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0])) slug = "sarif-" + slug;
        return Trim(slug, 100) + "-" + fingerprint[^12..];
    }

    private static string LocationKey(FindingLocation location) =>
        $"{location.Path}\0{location.Range?.Start.Line}\0{location.Range?.Start.Column}\0" +
        $"{location.Range?.End.Line}\0{location.Range?.End.Column}\0{location.SymbolId}";

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ??
        "Analyzer finding";

    private static string? MessageText(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var message)) return null;
        var value = String(message, "text") ?? String(message, "markdown");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static JsonElement RequireObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"SARIF property '{property}' must be an object.");
        return value;
    }

    private static string? String(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private sealed record Artifact(string Uri, string? BaseId);

    private sealed record SarifRule(
        string Id,
        string? Name,
        string? ShortDescription,
        string? FullDescription,
        string? Help,
        string? HelpUri,
        string? DefaultLevel,
        IReadOnlyDictionary<string, string> MessageStrings,
        JsonElement? Properties);

    private sealed record RunContext(
        string Root,
        IReadOnlyList<IReadOnlyList<SarifRule>> Rules,
        IReadOnlyList<Artifact> Artifacts,
        IReadOnlyDictionary<string, Artifact> UriBases);
}

internal static class AnalyzerCommand
{
    public static IReadOnlyList<string> Expand(
        string command,
        string repositoryRoot,
        string target,
        string reportPath)
    {
        var arguments = Split(command);
        return arguments.Select(argument => argument
            .Replace("{repositoryRoot}", repositoryRoot, StringComparison.Ordinal)
            .Replace("{target}", target, StringComparison.Ordinal)
            .Replace("{reportPath}", reportPath, StringComparison.Ordinal))
            .ToArray();
    }

    public static IReadOnlyList<string> Split(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Analyzer command cannot be empty.");
        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '\\' && quote == '"' && index + 1 < command.Length &&
                command[index + 1] is '"' or '\\')
            {
                current.Append(command[++index]);
                continue;
            }
            if (character is '"' or '\'')
            {
                if (quote == character) quote = null;
                else if (quote is null) quote = character;
                else current.Append(character);
                continue;
            }
            if (char.IsWhiteSpace(character) && quote is null)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (quote is not null) throw new ArgumentException("Analyzer command contains an incomplete quote.");
        if (current.Length > 0) result.Add(current.ToString());
        if (result.Count == 0) throw new ArgumentException("Analyzer command cannot be empty.");
        return result;
    }

    public static string ContainedPath(string root, string value)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
        if (!IsWithin(root, path)) throw new ArgumentException("Analyzer paths must remain inside the repository.");
        var current = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(root, path).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Analyzer paths cannot traverse a symbolic link or junction.");
        }
        return path;
    }

    public static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedPath, comparison) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    public static string OutputDetail(SensorCommandResult output)
    {
        var detail = string.Join(" ", new[] { output.StandardOutput, output.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));
        if (detail.Length == 0) return "The analyzer returned no diagnostic output.";
        return detail.Length <= 1000 ? detail : detail[..1000];
    }
}
