using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Executes an optional repository-owned analyzer command and ingests its SARIF 2.1.0 report.
/// The command is deliberately producer-neutral: provenance comes from each SARIF run.
/// </summary>
public sealed class SarifSensor : IReviewEvidenceSensor
{
    public const string SensorVersion = "1.0.0";
    private readonly ISensorCommandRunner commandRunner;
    private readonly string sensorId;

    public SarifSensor(ISensorCommandRunner? commandRunner = null) : this("sarif", commandRunner)
    {
    }

    internal SarifSensor(string sensorId, ISensorCommandRunner? commandRunner)
    {
        this.sensorId = sensorId;
        this.commandRunner = commandRunner ?? new ProcessSensorCommandRunner();
    }

    public string Id => sensorId;

    public string Version => SensorVersion;

    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sarif"] = "2.1.0",
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        var target = ResolveTarget(root, request);
        var configuration = request.Configuration ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!configuration.TryGetValue("reportPath", out var configuredReportPath) ||
            string.IsNullOrWhiteSpace(configuredReportPath))
        {
            return Unavailable(request, "SARIF sensor configuration requires reportPath.");
        }

        string reportPath;
        string workingDirectory;
        try
        {
            reportPath = ResolveContainedPath(root, configuredReportPath, "SARIF report");
            workingDirectory = configuration.TryGetValue("workingDirectory", out var configuredWorkingDirectory) &&
                               !string.IsNullOrWhiteSpace(configuredWorkingDirectory)
                ? ResolveContainedDirectory(root, configuredWorkingDirectory)
                : target;
        }
        catch (ArgumentException exception)
        {
            return Unavailable(request, exception.Message);
        }

        if (configuration.TryGetValue("command", out var configuredCommand) &&
            !string.IsNullOrWhiteSpace(configuredCommand))
        {
            var expanded = configuredCommand
                .Replace("{reportPath}", reportPath, StringComparison.Ordinal)
                .Replace("{repositoryRoot}", root, StringComparison.Ordinal)
                .Replace("{target}", target, StringComparison.Ordinal);
            IReadOnlyList<string> command;
            try
            {
                command = SplitCommand(expanded);
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
                        $"{command[0]} exited with code {output.ExitCode} without producing SARIF report '{configuredReportPath}'. " +
                        CommandDetail(output));
                }
            }
            catch (Exception exception) when (
                exception is SecurityScannerUnavailableException or IOException or InvalidOperationException)
            {
                return Unavailable(request, $"Analyzer is unavailable: {exception.Message}");
            }
        }

        if (!File.Exists(reportPath))
        {
            return Unavailable(request, $"SARIF report was not found: {configuredReportPath}");
        }

        try
        {
            await using var stream = File.OpenRead(reportPath);
            var findings = await ParseAsync(stream, root, Id, cancellationToken).ConfigureAwait(false);
            var producerVersions = findings
                .Where(finding => finding.Source is not null)
                .GroupBy(finding => finding.Source!.Producer, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(finding => finding.Source!.ProducerVersion)
                        .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version)) ?? "not reported",
                    StringComparer.Ordinal);
            producerVersions["sarif"] = "2.1.0";
            return new SensorScanResult(true, null, findings,
                Provenance(request, producerVersions));
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
        {
            throw new InvalidDataException("SARIF 2.1.0 report must contain a runs array.");
        }

        var findings = new List<ReviewFinding>();
        var runIndex = 0;
        foreach (var run in runs.EnumerateArray())
        {
            var driver = RequireObject(run, "tool", "driver");
            var producer = GetString(driver, "name");
            if (string.IsNullOrWhiteSpace(producer))
            {
                throw new InvalidDataException($"SARIF run {runIndex} has no tool.driver.name.");
            }
            var producerVersion = GetString(driver, "semanticVersion") ??
                                  GetString(driver, "version") ??
                                  GetString(driver, "dottedQuadFileVersion");
            var rules = ReadRules(run, driver);
            var bases = ReadUriBases(run);
            var artifacts = ReadArtifacts(run);
            if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                var resultIndex = 0;
                foreach (var result in results.EnumerateArray())
                {
                    if (GetString(result, "kind") is "pass" or "notApplicable")
                    {
                        resultIndex++;
                        continue;
                    }
                    findings.Add(MapResult(result, root, sensorId, runIndex, resultIndex,
                        producer, producerVersion, rules, bases, artifacts));
                    resultIndex++;
                }
            }
            runIndex++;
        }

        return findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations.FirstOrDefault()?.Range?.Start.Line ?? 0)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ReviewFinding MapResult(
        JsonElement result,
        string repositoryRoot,
        string sensorId,
        int runIndex,
        int resultIndex,
        string producer,
        string? producerVersion,
        RuleCatalogue rules,
        IReadOnlyDictionary<string, ArtifactReference> bases,
        IReadOnlyList<ArtifactReference> artifacts)
    {
        var rule = rules.Resolve(result);
        var ruleId = GetString(result, "ruleId") ??
                     (result.TryGetProperty("rule", out var ruleReference) ? GetString(ruleReference, "id") : null) ??
                     rule?.Id ??
                     "sarif:unclassified";
        var message = ResolveMessage(result, rule) ?? $"Analyzer {producer} reported {ruleId}.";
        var locations = ReadAllLocations(result, repositoryRoot, bases, artifacts);
        var level = GetString(result, "level") ?? rule?.DefaultLevel;
        var title = rule?.ShortDescription ?? FirstLine(message);
        var recommendation = rule?.Help ?? rule?.FullDescription ??
                             $"Review analyzer rule {ruleId} and address it when applicable.";
        var reportedFingerprint = ReadFingerprintSeed(result);
        var fingerprintSeed = reportedFingerprint is null
            ? $"{producer}\0{ruleId}\0{message}\0{string.Join('\0', locations.Select(FormatLocation))}"
            : $"{producer}\0{ruleId}\0{reportedFingerprint}";
        var fingerprint = "sha256:" + Sha256(fingerprintSeed);
        var id = BuildId(producer, ruleId, fingerprint);
        var evidence = JsonSerializer.Serialize(new
        {
            sarifRun = runIndex,
            sarifResult = resultIndex,
            producer,
            producerVersion,
            rule = rule is null
                ? new { id = ruleId, name = (string?)null, helpUri = (string?)null, properties = (JsonElement?)null }
                : new { id = rule.Id, name = rule.Name, helpUri = rule.HelpUri, properties = rule.Properties },
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
                metadataOmitted = "Rule properties exceeded the evidence size limit.",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        return new ReviewFinding(
            id,
            "analyzer",
            MapSeverity(level),
            Trim(title, 300),
            Trim(message, 10_000),
            Trim(recommendation, 10_000),
            locations,
            fingerprint,
            Trim(ruleId, 200),
            evidence,
            new FindingSource(FindingSourceKind.Deterministic, sensorId, producer, producerVersion, runIndex));
    }

    private static RuleCatalogue ReadRules(JsonElement run, JsonElement driver)
    {
        var components = new List<IReadOnlyList<SarifRule>> { ReadRuleArray(driver) };
        if (run.GetProperty("tool").TryGetProperty("extensions", out var extensions) &&
            extensions.ValueKind == JsonValueKind.Array)
        {
            components.AddRange(extensions.EnumerateArray().Select(ReadRuleArray));
        }
        return new RuleCatalogue(components);
    }

    private static IReadOnlyList<SarifRule> ReadRuleArray(JsonElement component)
    {
        if (!component.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return [];
        return rules.EnumerateArray().Select(rule => new SarifRule(
            GetString(rule, "id") ?? "sarif:unclassified",
            GetString(rule, "name"),
            GetMessageText(rule, "shortDescription"),
            GetMessageText(rule, "fullDescription"),
            GetMessageText(rule, "help"),
            GetString(rule, "helpUri"),
            rule.TryGetProperty("defaultConfiguration", out var configuration)
                ? GetString(configuration, "level")
                : null,
            ReadMessageStrings(rule),
            rule.TryGetProperty("properties", out var properties) ? properties.Clone() : null)).ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadMessageStrings(JsonElement rule)
    {
        if (!rule.TryGetProperty("messageStrings", out var strings) || strings.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        return strings.EnumerateObject()
            .Select(property => new KeyValuePair<string, string?>(
                property.Name, GetString(property.Value, "text") ?? GetString(property.Value, "markdown")))
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }

    private static string? ResolveMessage(JsonElement result, SarifRule? rule)
    {
        if (!result.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        var text = GetString(message, "text") ?? GetString(message, "markdown");
        if (text is null && rule is not null && GetString(message, "id") is { } id)
        {
            rule.MessageStrings.TryGetValue(id, out text);
        }
        if (text is null) return null;
        if (!message.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
            return text;
        var resolved = text;
        var index = 0;
        foreach (var argument in arguments.EnumerateArray())
        {
            resolved = resolved.Replace(
                "{" + index.ToString(CultureInfo.InvariantCulture) + "}",
                argument.GetString() ?? argument.GetRawText(),
                StringComparison.Ordinal);
            index++;
        }
        return resolved;
    }

    private static IReadOnlyList<FindingLocation> ReadAllLocations(
        JsonElement result,
        string repositoryRoot,
        IReadOnlyDictionary<string, ArtifactReference> bases,
        IReadOnlyList<ArtifactReference> artifacts)
    {
        var locations = new List<FindingLocation>();
        AddLocationArray(result, "locations", locations, repositoryRoot, bases, artifacts);
        AddLocationArray(result, "relatedLocations", locations, repositoryRoot, bases, artifacts);
        if (result.TryGetProperty("codeFlows", out var codeFlows) && codeFlows.ValueKind == JsonValueKind.Array)
        {
            foreach (var codeFlow in codeFlows.EnumerateArray())
            {
                if (!codeFlow.TryGetProperty("threadFlows", out var threadFlows) ||
                    threadFlows.ValueKind != JsonValueKind.Array) continue;
                foreach (var threadFlow in threadFlows.EnumerateArray())
                {
                    if (!threadFlow.TryGetProperty("locations", out var threadLocations) ||
                        threadLocations.ValueKind != JsonValueKind.Array) continue;
                    foreach (var wrapper in threadLocations.EnumerateArray())
                    {
                        if (wrapper.TryGetProperty("location", out var location) &&
                            TryMapLocation(location, repositoryRoot, bases, artifacts) is { } mapped)
                            locations.Add(mapped);
                    }
                }
            }
        }
        if (result.TryGetProperty("stacks", out var stacks) && stacks.ValueKind == JsonValueKind.Array)
        {
            foreach (var stack in stacks.EnumerateArray())
            {
                if (!stack.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array) continue;
                foreach (var frame in frames.EnumerateArray())
                {
                    if (frame.TryGetProperty("location", out var location) &&
                        TryMapLocation(location, repositoryRoot, bases, artifacts) is { } mapped)
                        locations.Add(mapped);
                }
            }
        }
        return locations.DistinctBy(FormatLocation, StringComparer.Ordinal).ToArray();
    }

    private static void AddLocationArray(
        JsonElement parent,
        string propertyName,
        ICollection<FindingLocation> target,
        string repositoryRoot,
        IReadOnlyDictionary<string, ArtifactReference> bases,
        IReadOnlyList<ArtifactReference> artifacts)
    {
        if (!parent.TryGetProperty(propertyName, out var locations) || locations.ValueKind != JsonValueKind.Array)
            return;
        foreach (var location in locations.EnumerateArray())
        {
            if (TryMapLocation(location, repositoryRoot, bases, artifacts) is { } mapped) target.Add(mapped);
        }
    }

    private static FindingLocation? TryMapLocation(
        JsonElement location,
        string repositoryRoot,
        IReadOnlyDictionary<string, ArtifactReference> bases,
        IReadOnlyList<ArtifactReference> artifacts)
    {
        if (!location.TryGetProperty("physicalLocation", out var physical) ||
            physical.ValueKind != JsonValueKind.Object) return null;
        var artifact = physical.TryGetProperty("artifactLocation", out var inlineArtifact)
            ? ReadArtifactReference(inlineArtifact)
            : null;
        if (artifact?.Index is >= 0 and var index && index < artifacts.Count)
        {
            var indexed = artifacts[index];
            artifact = new ArtifactReference(
                artifact.Uri ?? indexed.Uri,
                artifact.UriBaseId ?? indexed.UriBaseId,
                index);
        }
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Uri)) return null;
        var path = ResolveArtifactPath(artifact, repositoryRoot, bases);
        var range = physical.TryGetProperty("region", out var region) && region.ValueKind == JsonValueKind.Object
            ? ReadRange(region)
            : null;
        var symbol = location.TryGetProperty("logicalLocations", out var logical) &&
                     logical.ValueKind == JsonValueKind.Array
            ? logical.EnumerateArray().Select(item => GetString(item, "fullyQualifiedName") ?? GetString(item, "name"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
        return new FindingLocation(path, range, symbol);
    }

    private static FindingRange? ReadRange(JsonElement region)
    {
        var startLine = GetInt(region, "startLine");
        if (startLine is null or < 1) return null;
        var startColumn = Math.Max(1, GetInt(region, "startColumn") ?? 1);
        var endLine = Math.Max(startLine.Value, GetInt(region, "endLine") ?? startLine.Value);
        var endColumn = Math.Max(1, GetInt(region, "endColumn") ??
            (endLine == startLine ? startColumn : 1));
        return new FindingRange(
            new FindingPosition(startLine.Value, startColumn),
            new FindingPosition(endLine, endColumn));
    }

    private static IReadOnlyDictionary<string, ArtifactReference> ReadUriBases(JsonElement run)
    {
        if (!run.TryGetProperty("originalUriBaseIds", out var baseIds) || baseIds.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, ArtifactReference>(StringComparer.Ordinal);
        return baseIds.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ReadArtifactReference(property.Value) ?? new ArtifactReference(null, null, null),
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<ArtifactReference> ReadArtifacts(JsonElement run)
    {
        if (!run.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array) return [];
        return artifacts.EnumerateArray().Select(artifact =>
            artifact.TryGetProperty("location", out var location)
                ? ReadArtifactReference(location) ?? new ArtifactReference(null, null, null)
                : new ArtifactReference(null, null, null)).ToArray();
    }

    private static ArtifactReference? ReadArtifactReference(JsonElement location)
    {
        if (location.ValueKind != JsonValueKind.Object) return null;
        var uri = GetString(location, "uri");
        var baseId = GetString(location, "uriBaseId");
        var index = GetInt(location, "index");
        return uri is null && baseId is null && index is null ? null : new ArtifactReference(uri, baseId, index);
    }

    private static string ResolveArtifactPath(
        ArtifactReference artifact,
        string repositoryRoot,
        IReadOnlyDictionary<string, ArtifactReference> bases)
    {
        var uri = artifact.Uri!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var baseId = artifact.UriBaseId;
        while (baseId is not null && seen.Add(baseId) && bases.TryGetValue(baseId, out var item))
        {
            if (!string.IsNullOrWhiteSpace(item.Uri))
            {
                uri = CombineUri(item.Uri!, uri);
            }
            baseId = item.UriBaseId;
        }

        var unescaped = Uri.UnescapeDataString(uri).Replace('\\', '/');
        string absolute;
        if (Uri.TryCreate(unescaped, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            absolute = Path.GetFullPath(parsed.LocalPath);
        }
        else if (Path.IsPathRooted(unescaped))
        {
            absolute = Path.GetFullPath(unescaped);
        }
        else
        {
            absolute = Path.GetFullPath(Path.Combine(repositoryRoot,
                unescaped.Replace('/', Path.DirectorySeparatorChar)));
        }

        return IsWithin(repositoryRoot, absolute)
            ? Path.GetRelativePath(repositoryRoot, absolute).Replace('\\', '/')
            : unescaped.TrimStart('/');
    }

    private static string CombineUri(string baseUri, string relativeUri)
    {
        if (Uri.TryCreate(baseUri, UriKind.Absolute, out var absoluteBase) &&
            Uri.TryCreate(absoluteBase, relativeUri, out var combined))
            return combined.ToString();
        return baseUri.TrimEnd('/', '\\') + "/" + relativeUri.TrimStart('/', '\\');
    }

    private static FindingSeverity MapSeverity(string? level) => level?.ToLowerInvariant() switch
    {
        "error" => FindingSeverity.High,
        "warning" => FindingSeverity.Medium,
        "note" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private static string? ReadFingerprintSeed(JsonElement result)
    {
        foreach (var propertyName in new[] { "partialFingerprints", "fingerprints" })
        {
            if (!result.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Object)
                continue;
            var entries = values.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value.GetString()}")
                .ToArray();
            if (entries.Length > 0) return string.Join('\0', entries);
        }
        return null;
    }

    private SensorScanResult Unavailable(SensorScanRequest request, string reason) =>
        new(false, reason, [], Provenance(request,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["sarif"] = "2.1.0" }));

    private SensorProvenance Provenance(
        SensorScanRequest request,
        IReadOnlyDictionary<string, string> versions) =>
        new(Id, Version, request.Scope.ToString().ToLowerInvariant(),
            request.Scope == SensorScope.Repository ? "." : request.Path!,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), versions);

    private static string ResolveTarget(string root, SensorScanRequest request)
    {
        if (!Enum.IsDefined(request.Scope)) throw new ArgumentException("Unknown sensor scope.");
        if (request.Scope == SensorScope.Repository) return root;
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ArgumentException("Path scope requires a path.");
        return ResolveContainedDirectory(root, request.Path);
    }

    private static string ResolveContainedDirectory(string root, string path)
    {
        var target = ResolveContainedPath(root, path, "Sensor working directory");
        if (!Directory.Exists(target))
            throw new ArgumentException("Sensor working directory must be an existing directory inside the repository.");
        return target;
    }

    private static string ResolveContainedPath(string root, string path, string description)
    {
        var target = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!IsWithin(root, target))
            throw new ArgumentException($"{description} must be inside the repository.");
        RejectReparseTraversal(root, target, description);
        return target;
    }

    private static void RejectReparseTraversal(string root, string target, string description)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(current, target).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"{description} cannot traverse a symbolic link or junction.");
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedPath, comparison) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    internal static IReadOnlyList<string> SplitCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else if (character == '\\' && index + 1 < command.Length && command[index + 1] == quote)
                {
                    current.Append(command[++index]);
                }
                else
                {
                    current.Append(character);
                }
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (quote is not null) throw new ArgumentException("Analyzer command contains an unmatched quote.");
        if (current.Length > 0) result.Add(current.ToString());
        if (result.Count == 0) throw new ArgumentException("Analyzer command is empty.");
        return result;
    }

    private static JsonElement RequireObject(JsonElement root, string property, string nested)
    {
        if (!root.TryGetProperty(property, out var parent) || parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(nested, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"SARIF run must contain {property}.{nested}.");
        return value;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? GetMessageText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        return GetString(message, "text") ?? GetString(message, "markdown");
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;

    private static string FormatLocation(FindingLocation location) =>
        $"{location.Path}:{location.Range?.Start.Line ?? 0}:{location.Range?.Start.Column ?? 0}:" +
        $"{location.Range?.End.Line ?? 0}:{location.Range?.End.Column ?? 0}:{location.SymbolId}";

    private static string BuildId(string producer, string ruleId, string fingerprint)
    {
        var slug = new string($"{producer}-{ruleId}".ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-').ToArray()).Trim('-', '.', '_');
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0])) slug = "sarif-" + slug;
        if (slug.Length < 3) slug += "-finding";
        var suffix = fingerprint[^12..];
        var maximumPrefix = 127 - suffix.Length - 1;
        if (slug.Length > maximumPrefix) slug = slug[..maximumPrefix].TrimEnd('-', '.', '_');
        return $"{slug}-{suffix}";
    }

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string CommandDetail(SensorCommandResult output)
    {
        var detail = string.IsNullOrWhiteSpace(output.StandardError)
            ? output.StandardOutput
            : output.StandardError;
        detail = detail.Trim();
        return detail.Length == 0 ? "The analyzer returned no diagnostic output." : Trim(detail, 1000);
    }

    private sealed record ArtifactReference(string? Uri, string? UriBaseId, int? Index);

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

    private sealed class RuleCatalogue(IReadOnlyList<IReadOnlyList<SarifRule>> components)
    {
        public SarifRule? Resolve(JsonElement result)
        {
            var componentIndex = 0;
            if (result.TryGetProperty("rule", out var ruleReference) &&
                ruleReference.ValueKind == JsonValueKind.Object &&
                ruleReference.TryGetProperty("toolComponent", out var component) &&
                component.ValueKind == JsonValueKind.Object)
            {
                componentIndex = GetInt(component, "index") ?? 0;
            }
            if (componentIndex < 0 || componentIndex >= components.Count) componentIndex = 0;
            var rules = components[componentIndex];
            var ruleId = GetString(result, "ruleId") ??
                         (result.TryGetProperty("rule", out ruleReference)
                             ? GetString(ruleReference, "id")
                             : null);
            if (ruleId is not null)
            {
                var byId = rules.FirstOrDefault(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal));
                if (byId is not null) return byId;
            }
            var ruleIndex = GetInt(result, "ruleIndex") ??
                            (result.TryGetProperty("rule", out ruleReference)
                                ? GetInt(ruleReference, "index")
                                : null);
            return ruleIndex is >= 0 && ruleIndex < rules.Count ? rules[ruleIndex.Value] : null;
        }
    }
}
