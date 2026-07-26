using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record BoundaryInventory(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Sensor,
    string SensorVersion,
    IReadOnlyList<BoundaryEntry> Entries,
    IReadOnlyList<ReviewFinding> Findings);

public sealed record BoundaryEntry(
    string Id,
    string Kind,
    string Direction,
    string Name,
    string Transport,
    BoundarySourceLocation Location,
    BoundaryFact Reachability,
    BoundaryFact Authentication,
    BoundaryFact Authorization,
    IReadOnlyList<BoundaryInput> Inputs,
    BoundaryResponse Response,
    IReadOnlyList<string> SideEffects,
    BoundaryLimit RateLimit,
    BoundaryLimit SizeLimit,
    IReadOnlyList<BoundarySourceLocation> KnownConsumers,
    IReadOnlyList<string> Evidence);

public sealed record BoundarySourceLocation(string Path, int Line);

public sealed record BoundaryFact(string Value, IReadOnlyList<string> DerivedFrom);

public sealed record BoundaryInput(string Name, string Source, string Type, bool? Required);

public sealed record BoundaryResponse(string Shape, string? ContentType);

public sealed record BoundaryLimit(string Value, IReadOnlyList<string> DerivedFrom);

/// <summary>
/// Derives repository boundary facts from source and configuration. The inventory is
/// deliberately conservative: an unproved fact is recorded as unknown, never upgraded
/// by convention or by an agent's judgement.
/// </summary>
public sealed partial class BoundaryInventorySensor : IReviewSensor
{
    public const string SensorVersion = "1.0.0";
    public const string InventoryRelativePath = ".quality/boundaries/inventory.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Id => "boundaries";

    public string Version => SensorVersion;

    public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

    public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string>
        {
            ["boundary-analyzer"] = SensorVersion,
        }));

    public async Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var inventory = await InventoryAsync(request, cancellationToken).ConfigureAwait(false);
        return new SensorScanResult(
            true,
            null,
            inventory.Findings,
            new SensorProvenance(
                Id,
                Version,
                request.Scope.ToString().ToLowerInvariant(),
                request.Scope == SensorScope.Path ? request.Path ?? "." : ".",
                DateTimeOffset.UtcNow.ToString("O"),
                new Dictionary<string, string> { ["boundary-analyzer"] = Version }));
    }

    public async Task<BoundaryInventory> InventoryAsync(
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
        var sources = await ReadSourcesAsync(root, target, cancellationToken).ConfigureAwait(false);
        var context = new AnalysisContext(root, sources);
        var entries = new List<BoundaryEntry>();
        AnalyzeAspNet(context, entries);
        AnalyzeJavaScript(context, entries);
        AnalyzeBrowserEmbedding(context, entries);
        AnalyzeHostBindings(context, entries);
        AnalyzeProcessFileAndOutboundBoundaries(context, entries);
        AnalyzeErrorPolicies(context, entries);

        var ordered = entries
            .DistinctBy(entry => entry.Id, StringComparer.Ordinal)
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Location.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Location.Line)
            .ToArray();
        var findings = MechanicalChecks(ordered);
        var inventory = new BoundaryInventory(
            "https://quality.studio/schemas/boundary-inventory.v1.schema.json",
            1,
            Id,
            Version,
            ordered,
            findings);

        if (request.PersistMetadata && request.Scope == SensorScope.Repository)
        {
            await PersistAsync(root, inventory, cancellationToken).ConfigureAwait(false);
        }

        return inventory;
    }

    private static async Task<IReadOnlyList<SourceFile>> ReadSourcesAsync(
        string root,
        string target,
        CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var path in EnumerateFiles(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 2 * 1024 * 1024) continue;
                var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                files.Add(new SourceFile(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // An unreadable source cannot safely be guessed. Other readable sources
                // remain useful and any facts depending on this file stay unknown.
            }
        }
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> EnumerateFiles(string target)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".fs", ".vb", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
            ".html", ".htm", ".json", ".jsonc", ".yml", ".yaml", ".toml", ".xml", ".config",
        };
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".fixture.", StringComparison.OrdinalIgnoreCase) ||
                    name is "package-lock.json" or "npm-shrinkwrap.json")
                    continue;
                if (extensions.Contains(Path.GetExtension(file))) yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                if (!IsIgnoredDirectory(child)) pending.Push(child);
            }
        }
    }

    private static bool IsIgnoredDirectory(string path) =>
        Path.GetFileName(path) is ".git" or ".quality" or ".quality-studio" or "bin" or "obj" or
            "node_modules" or "dist" or "coverage" or ".angular" or ".next" or "TestResults" or
            "tests" or "test" or "__tests__";

    private static string ResolveTarget(string root, SensorScanRequest request)
    {
        if (request.Scope == SensorScope.Repository || string.IsNullOrWhiteSpace(request.Path)) return root;
        var target = Path.GetFullPath(Path.Combine(root, request.Path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, PathComparison) || !Directory.Exists(target))
            throw new ArgumentException("Boundary sensor path must be an existing directory inside the repository.", nameof(request));
        return target;
    }

    private static void AnalyzeAspNet(AnalysisContext context, ICollection<BoundaryEntry> entries)
    {
        foreach (var file in context.Sources.Where(source => source.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var groups = GroupPrefixes(file);
            foreach (Match match in AspNetMapRegex().Matches(file.Content))
            {
                var receiver = match.Groups["receiver"].Value;
                var operation = match.Groups["method"].Value;
                var method = operation.Length == 0 ? "ANY" : operation.ToUpperInvariant();
                var route = match.Groups["route"].Value;
                var prefix = groups.GetValueOrDefault(receiver)?.Prefix ?? string.Empty;
                var fullRoute = NormalizeRoute(prefix + route);
                var line = Line(file.Content, match.Index);
                var statement = StatementAt(file.Content, match.Index);
                var handler = HandlerName(statement);
                var handlerBody = handler is null ? statement : MethodText(file.Content, handler);
                var signature = handler is null ? statement : MethodSignature(file.Content, handler);
                var location = new BoundarySourceLocation(file.Path, line);
                var isApi = fullRoute.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
                var explicitlyAuthorized = statement.Contains("RequireAuthorization", StringComparison.Ordinal) ||
                                           groups.GetValueOrDefault(receiver)?.Authorized == true;
                var middlewareAuthenticated = isApi &&
                    file.Content.Contains("StartsWithSegments(\"/api\")", StringComparison.Ordinal) &&
                    (file.Content.Contains("Authenticate(context)", StringComparison.Ordinal) ||
                     file.Content.Contains("UseAuthentication()", StringComparison.Ordinal));
                var authenticated = explicitlyAuthorized || middlewareAuthenticated;
                var reachability = HostReachability(context);
                if (authenticated)
                {
                    reachability = new BoundaryFact("authenticated",
                        explicitlyAuthorized
                            ? [$"{file.Path}:{line} route/group calls RequireAuthorization"]
                            : [$"{file.Path} authenticates requests whose path starts with /api"]);
                }
                var auth = authenticated
                    ? new BoundaryFact("required", reachability.DerivedFrom)
                    : new BoundaryFact("none", [$"{file.Path}:{line} has no applicable authorization requirement"]);
                var authorization = DeriveAuthorization(file, fullRoute, explicitlyAuthorized, middlewareAuthenticated);
                var inputs = ParseDotNetInputs(signature, route);
                var effects = SideEffects(handlerBody);
                var rate = DeriveRateLimit(file, statement);
                var size = DeriveSizeLimit(file, method);
                var response = DeriveResponse(handlerBody);
                var consumers = KnownConsumers(context, method, fullRoute);
                var evidence = new List<string> { $"{receiver}.Map{operation}(\"{route}\")" };
                if (handler is not null) evidence.Add($"handler {handler}");
                var kind = fullRoute.Contains("webhook", StringComparison.OrdinalIgnoreCase)
                    ? "webhook"
                    : handlerBody.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
                      handlerBody.Contains("IAsyncEnumerable", StringComparison.Ordinal)
                        ? "sse"
                        : "http";
                entries.Add(new BoundaryEntry(
                    StableId("http", method, fullRoute),
                    kind,
                    "inbound",
                    $"{method} {fullRoute}",
                    kind == "sse" ? "sse" : "http",
                    location,
                    reachability,
                    auth,
                    authorization,
                    inputs,
                    response,
                    effects,
                    rate,
                    size,
                    consumers,
                    evidence));
            }

            AnalyzeMvcControllers(context, file, entries);

            foreach (Match match in SpecialAspNetRegex().Matches(file.Content))
            {
                var operation = match.Groups["operation"].Value;
                var kind = operation switch
                {
                    "UseStaticFiles" or "MapFallbackToFile" => "static-files",
                    "MapHealthChecks" => "health",
                    "MapHub" => "hub",
                    "UseWebSockets" => "websocket",
                    _ => "http",
                };
                var argument = match.Groups["argument"].Value;
                var line = Line(file.Content, match.Index);
                var entry = UnknownInbound(
                    StableId(kind, file.Path, line.ToString()),
                    kind,
                    string.IsNullOrWhiteSpace(argument) ? operation : $"{operation} {argument}",
                    kind is "hub" ? "signalr" : kind is "websocket" ? "websocket" : "http",
                    new BoundarySourceLocation(file.Path, line),
                    [$"{file.Path}:{line} calls {operation}"]);
                if (kind == "static-files")
                    entry = entry with
                    {
                        SideEffects = ["filesystem-read"],
                        Response = new BoundaryResponse("static-content", "derived-from-file"),
                    };
                entries.Add(entry);
            }

            foreach (Match match in HostedServiceRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                entries.Add(UnknownInbound(
                    StableId("scheduled-trigger", file.Path, line.ToString()),
                    "scheduled-trigger",
                    match.Groups["service"].Value,
                    "hosted-service",
                    new BoundarySourceLocation(file.Path, line),
                    [$"{file.Path}:{line} registers a hosted background service"]));
            }
        }
    }

    private static void AnalyzeMvcControllers(
        AnalysisContext context,
        SourceFile file,
        ICollection<BoundaryEntry> entries)
    {
        foreach (Match controller in ControllerRegex().Matches(file.Content))
        {
            var classOpen = file.Content.IndexOf('{', controller.Index + controller.Length);
            if (classOpen < 0) continue;
            var classClose = FindMatching(file.Content, classOpen, '{', '}');
            if (classClose < 0) continue;
            var controllerName = controller.Groups["name"].Value;
            var attributes = controller.Groups["attributes"].Value;
            var routeMatch = ControllerRouteRegex().Match(attributes);
            var prefix = routeMatch.Success ? routeMatch.Groups["route"].Value : "[controller]";
            prefix = prefix.Replace("[controller]",
                controllerName.EndsWith("Controller", StringComparison.Ordinal)
                    ? controllerName[..^"Controller".Length]
                    : controllerName,
                StringComparison.OrdinalIgnoreCase);
            var classAuthorized = attributes.Contains("[Authorize", StringComparison.Ordinal);
            var block = file.Content[classOpen..(classClose + 1)];
            foreach (Match action in ControllerActionRegex().Matches(block))
            {
                var actionAttributes = action.Groups["attributes"].Value;
                var verb = action.Groups["verb"].Value.ToUpperInvariant();
                var actionRoute = action.Groups["route"].Value;
                var route = NormalizeRoute(prefix + "/" + actionRoute);
                var absoluteIndex = classOpen + action.Index;
                var line = Line(file.Content, absoluteIndex);
                var actionOpen = file.Content.IndexOf('{', absoluteIndex + action.Length);
                var actionBody = string.Empty;
                if (actionOpen >= 0 && actionOpen < classClose)
                {
                    var actionClose = FindMatching(file.Content, actionOpen, '{', '}');
                    if (actionClose > actionOpen) actionBody = file.Content[actionOpen..(actionClose + 1)];
                }
                var allowsAnonymous = actionAttributes.Contains("[AllowAnonymous", StringComparison.Ordinal);
                var authorized = !allowsAnonymous &&
                                 (classAuthorized || actionAttributes.Contains("[Authorize", StringComparison.Ordinal));
                var derivation = authorized
                    ? $"{file.Path}:{line} applies [Authorize] to the controller or action"
                    : allowsAnonymous
                        ? $"{file.Path}:{line} applies [AllowAnonymous]"
                        : $"{file.Path}:{line} has no derived controller authorization attribute";
                var ratePolicy = Regex.Match(actionAttributes + attributes,
                    @"EnableRateLimiting\s*\(\s*""(?<policy>[^""]+)""",
                    RegexOptions.CultureInvariant);
                var rate = ratePolicy.Success
                    ? new BoundaryLimit("policy", [$"{file.Path}:{line} applies rate-limit policy '{ratePolicy.Groups["policy"].Value}'"])
                    : DeriveRateLimit(file, string.Empty);
                var size = Regex.IsMatch(actionAttributes, @"RequestSizeLimit|RequestFormLimits", RegexOptions.CultureInvariant)
                    ? new BoundaryLimit("action", [$"{file.Path}:{line} applies an MVC request size limit"])
                    : DeriveSizeLimit(file, verb);
                var kind = route.Contains("webhook", StringComparison.OrdinalIgnoreCase) ? "webhook"
                    : actionBody.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
                      action.Groups["return"].Value.Contains("IAsyncEnumerable", StringComparison.Ordinal)
                        ? "sse"
                        : "http";
                entries.Add(new BoundaryEntry(
                    StableId("http", verb, route),
                    kind,
                    "inbound",
                    $"{verb} {route}",
                    kind == "sse" ? "sse" : "http",
                    new BoundarySourceLocation(file.Path, line),
                    authorized ? new BoundaryFact("authenticated", [derivation]) : HostReachability(context),
                    new BoundaryFact(authorized ? "required" : allowsAnonymous ? "none" : "unknown", [derivation]),
                    new BoundaryFact(authorized ? "required" : allowsAnonymous ? "none" : "unknown", [derivation]),
                    ParseDotNetInputs("(" + action.Groups["parameters"].Value + ")", route),
                    new BoundaryResponse(action.Groups["return"].Value.Trim(), "application/json"),
                    SideEffects(actionBody),
                    rate,
                    size,
                    KnownConsumers(context, verb, route),
                    [$"{controllerName}.{action.Groups["name"].Value}", actionAttributes.Trim()]));
            }
        }
    }

    private static Dictionary<string, RouteGroup> GroupPrefixes(SourceFile file)
    {
        var result = new Dictionary<string, RouteGroup>(StringComparer.Ordinal);
        foreach (Match match in MapGroupRegex().Matches(file.Content))
        {
            var statement = StatementAt(file.Content, match.Index);
            result[match.Groups["name"].Value] = new RouteGroup(
                match.Groups["prefix"].Value,
                statement.Contains("RequireAuthorization", StringComparison.Ordinal));
        }
        return result;
    }

    private static BoundaryFact DeriveAuthorization(
        SourceFile file,
        string route,
        bool explicitlyAuthorized,
        bool middlewareAuthenticated)
    {
        if (middlewareAuthenticated &&
            (file.Content.Contains("CanAccess(repositoryId)", StringComparison.Ordinal) ||
             file.Content.Contains("CanAccess(RepositoryRegistry.DefaultRepositoryId)", StringComparison.Ordinal)))
        {
            return new BoundaryFact("repository-scoped",
                [$"{file.Path} checks the authenticated identity's repository access for /api routes"]);
        }
        if (explicitlyAuthorized)
            return new BoundaryFact("required", [$"{file.Path} applies RequireAuthorization to the route or route group"]);
        if (route.StartsWith("/api", StringComparison.OrdinalIgnoreCase) && middlewareAuthenticated)
            return new BoundaryFact("authenticated-only",
                [$"{file.Path} authenticates /api routes; no finer authorization was derived"]);
        return new BoundaryFact("none", [$"{file.Path} contains no applicable authorization check"]);
    }

    private static IReadOnlyList<BoundaryInput> ParseDotNetInputs(string signature, string route)
    {
        var result = new List<BoundaryInput>();
        var open = signature.IndexOf('(');
        if (open < 0) return result;
        var close = FindMatching(signature, open, '(', ')');
        if (close < 0) return result;
        var parameters = SplitTopLevel(signature[(open + 1)..close]);
        var routeNames = RouteParameterRegex().Matches(route).Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in parameters)
        {
            var parameter = raw.Trim();
            if (parameter.Length == 0) continue;
            parameter = Regex.Replace(parameter, @"\[[^\]]+\]\s*", string.Empty);
            var tokens = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            var name = tokens[^1].Split('=')[0].Trim();
            var type = string.Join(' ', tokens[..^1]).Replace("ref ", string.Empty, StringComparison.Ordinal)
                .Replace("out ", string.Empty, StringComparison.Ordinal);
            if (IsServiceParameter(type)) continue;
            var source = parameter.Contains("[FromHeader", StringComparison.Ordinal) ? "header"
                : parameter.Contains("[FromBody", StringComparison.Ordinal) ? "body"
                : parameter.Contains("[FromRoute", StringComparison.Ordinal) || routeNames.Contains(name) ? "route"
                : IsBodyType(type) ? "body"
                : "query";
            result.Add(new BoundaryInput(name, source, type, type.EndsWith('?') ? false : null));
        }
        return result;
    }

    private static bool IsServiceParameter(string type) =>
        type.Contains("HttpContext", StringComparison.Ordinal) ||
        type.Contains("CancellationToken", StringComparison.Ordinal) ||
        type.Contains("ILogger", StringComparison.Ordinal) ||
        type.Contains("RepositoryRegistry", StringComparison.Ordinal) ||
        type.Contains("Service", StringComparison.Ordinal) ||
        type.Contains("Store", StringComparison.Ordinal) ||
        type.Contains("Resolver", StringComparison.Ordinal) ||
        type.Contains("Cache", StringComparison.Ordinal) ||
        type.Contains("Scanner", StringComparison.Ordinal) ||
        type.Contains("Options", StringComparison.Ordinal) ||
        type.Contains("Client", StringComparison.Ordinal) ||
        type.Contains("Quota", StringComparison.Ordinal) ||
        type.Contains("SensorRegistry", StringComparison.Ordinal) ||
        type.Contains("InputResolver", StringComparison.Ordinal) ||
        type.Contains("Guideline", StringComparison.Ordinal);

    private static bool IsBodyType(string type) =>
        type.EndsWith("Request", StringComparison.Ordinal) ||
        type.EndsWith("Draft", StringComparison.Ordinal) ||
        (!type.StartsWith("string", StringComparison.OrdinalIgnoreCase) &&
         !type.StartsWith("bool", StringComparison.OrdinalIgnoreCase) &&
         !type.StartsWith("int", StringComparison.OrdinalIgnoreCase) &&
         !type.StartsWith("long", StringComparison.OrdinalIgnoreCase) &&
         !type.StartsWith("DateTime", StringComparison.OrdinalIgnoreCase));

    private static BoundaryResponse DeriveResponse(string body)
    {
        var shapes = new List<string>();
        if (body.Contains("Results.Ok", StringComparison.Ordinal)) shapes.Add("200");
        if (body.Contains("Results.Created", StringComparison.Ordinal)) shapes.Add("201");
        if (body.Contains("Results.Accepted", StringComparison.Ordinal)) shapes.Add("202");
        if (body.Contains("Results.NoContent", StringComparison.Ordinal)) shapes.Add("204");
        if (body.Contains("Results.NotFound", StringComparison.Ordinal)) shapes.Add("404");
        if (body.Contains("Results.Problem", StringComparison.Ordinal)) shapes.Add("problem-details");
        return new BoundaryResponse(shapes.Count == 0 ? "unknown" : string.Join("|", shapes.Distinct()), "application/json");
    }

    private static IReadOnlyList<string> SideEffects(string text)
    {
        var effects = new List<string>();
        if (Regex.IsMatch(text, @"\b(File|Directory)\.(Write|Move|Delete|Create|OpenWrite)|CreateDirectory", RegexOptions.CultureInvariant))
            effects.Add("filesystem-write");
        if (Regex.IsMatch(text, @"\b(File|Directory)\.(Read|Open|Enumerate)|ResolveFile|NormalizeRelativePath", RegexOptions.CultureInvariant))
            effects.Add("filesystem-read");
        if (Regex.IsMatch(text, @"Process(Start|StartInfo)|\.Start\(\)|\b(?:spawn|exec|execFile)\s*\(", RegexOptions.CultureInvariant))
            effects.Add("process");
        if (Regex.IsMatch(text, @"HttpClient|SendAsync|GetAsync|PostAsync|CreateTaskAsync|GetProjectsAsync", RegexOptions.CultureInvariant))
            effects.Add("third-party-call");
        if (Regex.IsMatch(text, @"CreateAsync|UpdateAsync|ArchiveAsync|Save|Write|Delete|Mutate|Install|Start\(|Pause\(|Resume\(|Cancel\(", RegexOptions.CultureInvariant))
            effects.Add("state-mutation");
        if (Regex.IsMatch(text, @"Review|Handover|Quota|CreateTask", RegexOptions.CultureInvariant))
            effects.Add("spends-money-or-quota");
        return effects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static BoundaryLimit DeriveRateLimit(SourceFile file, string statement)
    {
        var policy = RequireRateRegex().Match(statement);
        if (policy.Success)
            return new BoundaryLimit("policy",
                [$"{file.Path} applies rate-limit policy '{policy.Groups["policy"].Value}'"]);
        if (file.Content.Contains("GlobalLimiter", StringComparison.Ordinal) &&
            file.Content.Contains("UseRateLimiter()", StringComparison.Ordinal))
            return new BoundaryLimit("global",
                [$"{file.Path} configures GlobalLimiter and calls UseRateLimiter"]);
        return new BoundaryLimit("absent", [$"{file.Path} has no derived global or route rate limit"]);
    }

    private static BoundaryLimit DeriveSizeLimit(SourceFile file, string method)
    {
        if (file.Content.Contains("MaxRequestBodySize", StringComparison.Ordinal) ||
            file.Content.Contains("MaxRequestBodyBytes", StringComparison.Ordinal) ||
            file.Content.Contains("RequestSizeLimit", StringComparison.Ordinal))
            return new BoundaryLimit("global",
                [$"{file.Path} configures or enforces a maximum request body size"]);
        return new BoundaryLimit(method is "GET" or "HEAD" ? "not-applicable" : "absent",
            [$"{file.Path} has no derived request body size limit"]);
    }

    private static void AnalyzeJavaScript(AnalysisContext context, ICollection<BoundaryEntry> entries)
    {
        foreach (var file in context.Sources.Where(IsJavaScript))
        {
            foreach (Match match in NodeRouteRegex().Matches(file.Content))
            {
                var receiver = match.Groups["receiver"].Value;
                if (receiver is "http" or "https" or "axios" or "client") continue;
                var method = match.Groups["method"].Value.ToUpperInvariant();
                var route = match.Groups["route"].Value;
                var line = Line(file.Content, match.Index);
                var statement = StatementAt(file.Content, match.Index);
                var routeContext = file.Content[match.Index..Math.Min(file.Content.Length, match.Index + 1500)];
                var auth = Regex.IsMatch(statement, @"auth|passport|jwt|session", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    ? new BoundaryFact("required", [$"{file.Path}:{line} route middleware names an authentication control"])
                    : new BoundaryFact("unknown", [$"{file.Path}:{line} has no mechanically recognized authentication control"]);
                var kind = route.Contains("webhook", StringComparison.OrdinalIgnoreCase)
                    ? "webhook"
                    : routeContext.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                        ? "sse"
                        : "http";
                entries.Add(new BoundaryEntry(
                    StableId("http", method, route),
                    kind,
                    "inbound",
                    $"{method} {route}",
                    kind == "sse" ? "sse" : "http",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("unknown", [$"{file.Path}:{line} registers a route; host binding was not joined"]),
                    auth,
                    new BoundaryFact("unknown", [$"{file.Path}:{line} does not prove an authorization policy"]),
                    [new BoundaryInput("request", "request", "unknown", null)],
                    new BoundaryResponse("unknown", null),
                    SideEffects(statement),
                    HasJavaScriptLimit(file, "rate") ? new BoundaryLimit("applied", [$"{file.Path} configures rate limiting"])
                        : new BoundaryLimit("absent", [$"{file.Path} has no recognized rate limiter"]),
                    HasJavaScriptLimit(file, "size") ? new BoundaryLimit("applied", [$"{file.Path} configures a body size limit"])
                        : new BoundaryLimit("absent", [$"{file.Path} has no recognized body size limit"]),
                    KnownConsumers(context, method, route),
                    [$"{receiver}.{match.Groups["method"].Value}('{route}')"]));
            }

            foreach (Match match in JavaScriptNetworkSurfaceRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var operation = match.Groups["operation"].Value;
                var kind = operation.Contains("static", StringComparison.OrdinalIgnoreCase) ? "static-files" : "websocket";
                entries.Add(UnknownInbound(
                    StableId(kind, file.Path, line.ToString()),
                    kind,
                    operation,
                    kind == "websocket" ? "websocket" : "http",
                    new BoundarySourceLocation(file.Path, line),
                    [$"{file.Path}:{line} registers {operation}"]));
            }

            if (!file.Path.Contains(".worker.", StringComparison.OrdinalIgnoreCase))
            foreach (Match match in BrowserMessageRegex().Matches(file.Content))
            {
                var direction = match.Groups["receive"].Success ? "inbound" : "outbound";
                var line = Line(file.Content, match.Index);
                var statement = StatementAt(file.Content, match.Index);
                var target = match.Groups["target"].Success ? match.Groups["target"].Value : "message event";
                entries.Add(new BoundaryEntry(
                    StableId("browser-message", file.Path, line.ToString()),
                    "browser-message",
                    direction,
                    direction == "inbound" ? "postMessage receiver" : $"postMessage to {target}",
                    "postmessage",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact(direction == "inbound" ? "public" : "hosting-page",
                        [$"{file.Path}:{line} uses the browser postMessage boundary"]),
                    new BoundaryFact("none", [$"{file.Path}:{line} contains no transport authentication"]),
                    new BoundaryFact(
                        statement.Contains(".origin", StringComparison.Ordinal) || statement.Contains("origin", StringComparison.OrdinalIgnoreCase)
                            ? "origin-checked"
                            : "none",
                        [$"{file.Path}:{line} " +
                         (statement.Contains("origin", StringComparison.OrdinalIgnoreCase)
                             ? "references message origin"
                             : "has no derived origin check")]),
                    direction == "inbound" ? [new BoundaryInput("event.data", "message", "unknown", null)] : [],
                    new BoundaryResponse("message", null),
                    [],
                    new BoundaryLimit("absent", [$"{file.Path}:{line} has no recognized message rate limit"]),
                    new BoundaryLimit("absent", [$"{file.Path}:{line} has no recognized message size limit"]),
                    [],
                    [statement.Trim()]));
            }

            foreach (Match match in JavaScriptTriggerRegex().Matches(file.Content))
            {
                var operation = match.Groups["operation"].Value;
                var kind = operation.Contains("cron", StringComparison.OrdinalIgnoreCase) ||
                           operation.Contains("schedule", StringComparison.OrdinalIgnoreCase)
                    ? "scheduled-trigger"
                    : operation.Contains("watch", StringComparison.OrdinalIgnoreCase)
                        ? "file-trigger"
                        : "message-consumer";
                var line = Line(file.Content, match.Index);
                entries.Add(UnknownInbound(
                    StableId(kind, file.Path, line.ToString()),
                    kind,
                    operation,
                    kind == "message-consumer" ? "message-queue" : kind,
                    new BoundarySourceLocation(file.Path, line),
                    [$"{file.Path}:{line} calls {operation}"]));
            }
        }
    }

    private static void AnalyzeBrowserEmbedding(AnalysisContext context, ICollection<BoundaryEntry> entries)
    {
        foreach (var file in context.Sources)
        {
            foreach (Match match in IframeRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var target = match.Groups["target"].Value;
                entries.Add(new BoundaryEntry(
                    StableId("iframe", file.Path, line.ToString()),
                    "iframe",
                    "outbound",
                    $"iframe {target}",
                    "browser",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("browser-visible", [$"{file.Path}:{line} embeds an iframe"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} iframe authentication belongs to the target"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} iframe authorization belongs to the target"]),
                    [new BoundaryInput("src", target.Contains("{{", StringComparison.Ordinal) ||
                                                     target.Contains("${", StringComparison.Ordinal)
                        ? "template"
                        : "constant", "uri", true)],
                    new BoundaryResponse("embedded-document", "text/html"),
                    ["third-party-call"],
                    new BoundaryLimit("not-applicable", []),
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} has no derived iframe message/document size limit"]),
                    [],
                    [match.Value]));
            }

            foreach (Match match in FramePolicyRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var value = match.Groups["value"].Value.Trim();
                var policy = value.Contains('*') ? "permissive"
                    : value.Contains("'none'", StringComparison.OrdinalIgnoreCase) ||
                      value.Contains("DENY", StringComparison.OrdinalIgnoreCase) ? "denied"
                    : "allowlist";
                entries.Add(new BoundaryEntry(
                    StableId("iframe-policy", file.Path, line.ToString()),
                    "iframe-policy",
                    "inbound",
                    $"embedding policy {value}",
                    "browser",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("hosting-page", [$"{file.Path}:{line} configures iframe embedding"]),
                    new BoundaryFact("not-applicable", []),
                    new BoundaryFact(policy, [$"{file.Path}:{line} sets a frame embedding policy"]),
                    [],
                    new BoundaryResponse("browser-policy", null),
                    [],
                    new BoundaryLimit("not-applicable", []),
                    new BoundaryLimit("not-applicable", []),
                    [],
                    [match.Value]));
            }
        }
    }

    private static bool HasJavaScriptLimit(SourceFile file, string kind) =>
        kind == "rate"
            ? Regex.IsMatch(file.Content, @"express-rate-limit|rateLimit\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : Regex.IsMatch(file.Content, @"express\.(json|raw|text|urlencoded)\s*\([^)]*limit|bodyParser\.[a-z]+\s*\([^)]*limit",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void AnalyzeHostBindings(AnalysisContext context, ICollection<BoundaryEntry> entries)
    {
        foreach (var file in context.Sources)
        {
            foreach (Match match in UrlBindingRegex().Matches(file.Content))
            {
                if (!IsHostBinding(file.Content, match.Index)) continue;
                var value = match.Groups["url"].Value;
                if (!value.Contains("://", StringComparison.Ordinal)) continue;
                var line = Line(file.Content, match.Index);
                var host = Regex.Match(value, @"://(?<host>[^:/""'`$]+)").Groups["host"].Value;
                var reachability = IsLoopback(host)
                    ? new BoundaryFact("loopback-only", [$"{file.Path}:{line} binds host {host}"])
                    : host is "0.0.0.0" or "::" or "+"
                        ? new BoundaryFact("public", [$"{file.Path}:{line} binds wildcard host {host}"])
                        : new BoundaryFact("unknown", [$"{file.Path}:{line} binds a variable or non-literal host"]);
                entries.Add(new BoundaryEntry(
                    StableId("host-listener", file.Path, line.ToString(), value),
                    "host-listener",
                    "inbound",
                    value,
                    value.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
                    new BoundarySourceLocation(file.Path, line),
                    reachability,
                    new BoundaryFact("not-applicable", [$"{file.Path}:{line} is a host binding"]),
                    new BoundaryFact("not-applicable", [$"{file.Path}:{line} is a host binding"]),
                    [],
                    new BoundaryResponse("listener", null),
                    [],
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not itself establish connection limiting"]),
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not itself establish request sizing"]),
                    [],
                    [value]));
            }

            foreach (Match match in ListenRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var host = match.Groups["host"].Value;
                var reachability = IsLoopback(host)
                    ? new BoundaryFact("loopback-only", [$"{file.Path}:{line} calls listen with {host}"])
                    : string.IsNullOrWhiteSpace(host)
                        ? new BoundaryFact("public", [$"{file.Path}:{line} omits the listen host"])
                        : new BoundaryFact("unknown", [$"{file.Path}:{line} listen host is not a known loopback literal"]);
                entries.Add(new BoundaryEntry(
                    StableId("host-listener", file.Path, line.ToString()),
                    "host-listener",
                    "inbound",
                    $"listen({match.Groups["port"].Value}{(host.Length > 0 ? $", {host}" : string.Empty)})",
                    "tcp",
                    new BoundarySourceLocation(file.Path, line),
                    reachability,
                    new BoundaryFact("not-applicable", [$"{file.Path}:{line} is a host binding"]),
                    new BoundaryFact("not-applicable", [$"{file.Path}:{line} is a host binding"]),
                    [],
                    new BoundaryResponse("listener", null),
                    [],
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not prove a connection limit"]),
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not prove a request size limit"]),
                    [],
                    [match.Value]));
            }

            foreach (Match match in CorsRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var value = match.Groups["value"].Value;
                entries.Add(new BoundaryEntry(
                    StableId("cors", file.Path, line.ToString()),
                    "cors-policy",
                    "inbound",
                    value.Length == 0 ? "CORS policy" : $"CORS {value}",
                    "browser",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("browser-origins", [$"{file.Path}:{line} configures CORS"]),
                    new BoundaryFact("not-applicable", [$"{file.Path}:{line} is a browser origin policy"]),
                    new BoundaryFact(value == "*" || match.Value.Contains("AllowAnyOrigin", StringComparison.Ordinal)
                            ? "permissive"
                            : "origin-allowlist",
                        [$"{file.Path}:{line} " + (value == "*" || match.Value.Contains("AllowAnyOrigin", StringComparison.Ordinal)
                            ? "allows any origin"
                            : "uses an origin allowlist or configured origins")]),
                    [],
                    new BoundaryResponse("cors-policy", null),
                    [],
                    new BoundaryLimit("not-applicable", []),
                    new BoundaryLimit("not-applicable", []),
                    [],
                    [match.Value]));
            }
        }
    }

    private static void AnalyzeProcessFileAndOutboundBoundaries(
        AnalysisContext context,
        ICollection<BoundaryEntry> entries)
    {
        foreach (var file in context.Sources)
        {
            foreach (Match match in ProcessRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var executable = match.Groups["executable"].Value.Trim();
                var inputs = new List<BoundaryInput>
                {
                    new("executable", InferInputSource(executable), "string", null),
                };
                var argumentContext = file.Content[match.Index..Math.Min(file.Content.Length, match.Index + 4000)];
                var processStart = argumentContext.IndexOf(".Start()", StringComparison.Ordinal);
                if (processStart >= 0) argumentContext = argumentContext[..processStart];
                var argumentIndex = 0;
                foreach (Match argument in ProcessArgumentRegex().Matches(argumentContext))
                {
                    var expression = argument.Groups["argument"].Value.Trim();
                    inputs.Add(new BoundaryInput($"argument{argumentIndex++}", InferInputSource(expression), "string", null));
                }
                entries.Add(new BoundaryEntry(
                    StableId("process", file.Path, line.ToString()),
                    "process",
                    "outbound",
                    $"process {executable}",
                    "subprocess",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("internal-callable", [$"{file.Path}:{line} constructs a process invocation"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} subprocess authentication is inherited from callers"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} subprocess authorization is inherited from callers"]),
                    inputs,
                    new BoundaryResponse("exit-code/stdout/stderr", null),
                    ["process"],
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not prove invocation throttling"]),
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not prove output sizing"]),
                    ProcessConsumers(context, file),
                    [match.Value]));
            }

            foreach (Match match in OutboundRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                var target = match.Groups["target"].Value.Trim();
                entries.Add(new BoundaryEntry(
                    StableId("outbound-http", file.Path, line.ToString()),
                    "outbound-http",
                    "outbound",
                    $"HTTP {target}",
                    "http",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("internal-callable", [$"{file.Path}:{line} performs an outbound HTTP call"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} outbound credentials are configured at the sink"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} target authorization is not statically known"]),
                    [new BoundaryInput("target", InferInputSource(target), "uri", null)],
                    new BoundaryResponse("remote-response", null),
                    ["third-party-call"],
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not prove caller throttling"]),
                    new BoundaryLimit("unknown", [$"{file.Path}:{line} does not prove response sizing"]),
                    [],
                    [match.Value]));
            }

            foreach (Match match in FileWatcherRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                entries.Add(UnknownInbound(
                    StableId("file-trigger", file.Path, line.ToString()),
                    "file-trigger",
                    "filesystem watcher",
                    "filesystem",
                    new BoundarySourceLocation(file.Path, line),
                    [$"{file.Path}:{line} creates or registers a filesystem watcher"]));
            }
        }
    }

    private static void AnalyzeErrorPolicies(AnalysisContext context, ICollection<BoundaryEntry> entries)
    {
        foreach (var file in context.Sources)
        {
            foreach (Match match in ExceptionDetailRegex().Matches(file.Content))
            {
                var line = Line(file.Content, match.Index);
                entries.Add(new BoundaryEntry(
                    StableId("error-policy", file.Path, line.ToString()),
                    "error-policy",
                    "outbound",
                    "exception detail in response",
                    "http",
                    new BoundarySourceLocation(file.Path, line),
                    new BoundaryFact("caller-visible", [$"{file.Path}:{line} constructs a response from exception detail"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} inherits route authentication"]),
                    new BoundaryFact("caller-dependent", [$"{file.Path}:{line} inherits route authorization"]),
                    [],
                    new BoundaryResponse("exception-detail", null),
                    ["information-disclosure"],
                    new BoundaryLimit("not-applicable", []),
                    new BoundaryLimit("not-applicable", []),
                    [],
                    [match.Value]));
            }
        }
    }

    private static IReadOnlyList<ReviewFinding> MechanicalChecks(IReadOnlyList<BoundaryEntry> entries)
    {
        var findings = new List<ReviewFinding>();
        foreach (var entry in entries)
        {
            if (entry.Direction == "inbound" &&
                entry.Kind is "http" or "sse" or "webhook" or "hub" or "websocket" or "message-consumer" or "browser-message" &&
                entry.Authentication.Value is "none" or "unknown")
            {
                findings.Add(Finding(entry, "boundary/missing-authorization", FindingSeverity.High,
                    "Inbound boundary has no derived authorization",
                    $"{entry.Name} is externally callable, but the inventory could not derive an authentication and authorization requirement.",
                    "Apply an explicit authentication and authorization policy, or document and mechanically encode the narrow public exception."));
            }

            if (entry.Kind == "cors-policy" && entry.Authorization.Value == "permissive")
            {
                findings.Add(Finding(entry, "boundary/permissive-cors", FindingSeverity.High,
                    "CORS policy allows arbitrary origins",
                    $"{entry.Name} permits any browser origin.",
                    "Replace the wildcard with a repository-owned allowlist of exact trusted origins."));
            }

            if (entry.Kind == "error-policy" && entry.Response.Shape == "exception-detail")
            {
                findings.Add(Finding(entry, "boundary/exception-detail-response", FindingSeverity.High,
                    "Error response carries exception detail",
                    $"{entry.Location.Path} constructs a caller-visible response from an exception message or stack.",
                    "Log diagnostic detail server-side and return a fixed public problem title/detail."));
            }

            if (entry.Direction == "inbound" && entry.SideEffects.Count > 0 &&
                entry.Authentication.Value is "none" or "unknown")
            {
                findings.Add(Finding(entry, "boundary/unauthenticated-side-effect", FindingSeverity.Critical,
                    "Unauthenticated boundary has side effects",
                    $"{entry.Name} has side effects ({string.Join(", ", entry.SideEffects)}) without derived authentication.",
                    "Require authentication and least-privilege authorization before the side effect."));
            }

            if (entry.Kind is "http" or "sse" or "webhook" or "hub" or "websocket" or "message-consumer")
            {
                if (entry.RateLimit.Value == "absent")
                {
                    findings.Add(Finding(entry, "boundary/missing-rate-limit", FindingSeverity.Medium,
                        "Boundary has no derived rate limit",
                        $"{entry.Name} has no global or entry-specific rate limit in the inventory.",
                        "Apply a bounded global limiter and a tighter caller-partitioned policy to costly operations."));
                }
                if (entry.SizeLimit.Value == "absent")
                {
                    findings.Add(Finding(entry, "boundary/missing-size-limit", FindingSeverity.Medium,
                        "Boundary has no derived size limit",
                        $"{entry.Name} accepts input without a derived maximum size.",
                        "Enforce a transport-level and parser-level size limit."));
                }
            }

            if (entry.Inputs.Any(input => input.Source is "route" or "query" or "body" or "request" or "message") &&
                entry.SideEffects.Any(effect => effect is "filesystem-read" or "filesystem-write" or "process"))
            {
                findings.Add(Finding(entry, "boundary/request-to-system-sink", FindingSeverity.High,
                    "Request-bound value reaches a filesystem or process surface",
                    $"{entry.Name} combines request input with {string.Join(", ", entry.SideEffects.Where(effect => effect.Contains("filesystem", StringComparison.Ordinal) || effect == "process"))}.",
                    "Keep path/process arguments on a confined allowlisted data flow and verify the confinement before the sink."));
            }
        }

        return findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations[0].Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations[0].Range?.Start.Line ?? 0)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ReviewFinding Finding(
        BoundaryEntry entry,
        string ruleId,
        FindingSeverity severity,
        string title,
        string description,
        string recommendation)
    {
        var fingerprint = Hash($"boundaries\0{ruleId}\0{entry.Id}");
        var location = new FindingLocation(entry.Location.Path,
            new FindingRange(
                new FindingPosition(entry.Location.Line, 1),
                new FindingPosition(entry.Location.Line, 1)));
        var evidence = JsonSerializer.Serialize(new
        {
            boundaryId = entry.Id,
            entry.Reachability,
            entry.Authentication,
            entry.Authorization,
            entry.RateLimit,
            entry.SizeLimit,
            entry.SideEffects,
        }, JsonOptions);
        return new ReviewFinding(
            $"boundary-{fingerprint[7..19]}",
            "boundaries",
            severity,
            title,
            description,
            recommendation,
            [location],
            fingerprint,
            ruleId,
            evidence);
    }

    private static BoundaryEntry UnknownInbound(
        string id,
        string kind,
        string name,
        string transport,
        BoundarySourceLocation location,
        IReadOnlyList<string> evidence) =>
        new(
            id,
            kind,
            "inbound",
            name,
            transport,
            location,
            new BoundaryFact("unknown", evidence),
            new BoundaryFact("unknown", [$"{location.Path}:{location.Line} has no derived authentication fact"]),
            new BoundaryFact("unknown", [$"{location.Path}:{location.Line} has no derived authorization fact"]),
            [new BoundaryInput("payload", kind switch
            {
                "file-trigger" => "filesystem",
                "scheduled-trigger" => "schedule",
                "static-files" => "route",
                _ => "message",
            }, "unknown", null)],
            new BoundaryResponse("unknown", null),
            [],
            new BoundaryLimit("unknown", [$"{location.Path}:{location.Line} has no derived rate-limit fact"]),
            new BoundaryLimit("unknown", [$"{location.Path}:{location.Line} has no derived size-limit fact"]),
            [],
            evidence);

    private static BoundaryFact HostReachability(AnalysisContext context)
    {
        var listeners = context.Sources.SelectMany(file => UrlBindingRegex().Matches(file.Content).Cast<Match>()
            .Where(match => IsHostBinding(file.Content, match.Index))
            .Select(match => match.Groups["url"].Value)).ToArray();
        if (listeners.Length == 0)
            return new BoundaryFact("unknown", ["No literal host binding was joined to this route"]);
        if (listeners.All(value => value.Contains("127.0.0.1", StringComparison.Ordinal) ||
                                   value.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                                   value.Contains("[::1]", StringComparison.Ordinal)))
            return new BoundaryFact("loopback-only", ["All derived repository host bindings use loopback addresses"]);
        if (listeners.Any(value => value.Contains("0.0.0.0", StringComparison.Ordinal) ||
                                   value.Contains("://+:", StringComparison.Ordinal)))
            return new BoundaryFact("public", ["A derived repository host binding uses a wildcard address"]);
        return new BoundaryFact("unknown", ["Repository host bindings include unresolved or non-loopback values"]);
    }

    private static IReadOnlyList<BoundarySourceLocation> KnownConsumers(
        AnalysisContext context,
        string method,
        string route)
    {
        var literalPrefix = route.Split('{')[0].TrimEnd('/');
        if (literalPrefix.Length < 2) return [];
        var result = new List<BoundarySourceLocation>();
        foreach (var file in context.Sources.Where(IsJavaScript))
        {
            foreach (var (line, text) in file.Lines())
            {
                if (ClientRouteMention(text, route) &&
                    Regex.IsMatch(text,
                        $@"\.{Regex.Escape(method.ToLowerInvariant())}(?:<[^>]+>)?\s*\(",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    result.Add(new BoundarySourceLocation(file.Path, line));
            }
        }
        return result.Distinct().OrderBy(location => location.Path, StringComparer.Ordinal).ThenBy(location => location.Line).ToArray();
    }

    private static bool ClientRouteMention(string text, string route)
    {
        var candidates = new List<string> { route };
        if (route.StartsWith("/api/", StringComparison.Ordinal)) candidates.Add(route[4..]);
        var repositoryPrefix = Regex.Match(route, @"^/api/repos/\{[^}]+\}(?<tail>/.*)$",
            RegexOptions.CultureInvariant);
        if (repositoryPrefix.Success) candidates.Add(repositoryPrefix.Groups["tail"].Value);
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var pattern = Regex.Replace(
                Regex.Escape(candidate),
                @"\\\{[^}]+\\\}",
                @"(?:\$\{[^}]+\}|[^/`'""?]+)",
                RegexOptions.CultureInvariant);
            if (Regex.IsMatch(text, pattern + @"(?=$|[?`'""),}\]])", RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<BoundarySourceLocation> ProcessConsumers(AnalysisContext context, SourceFile processFile)
    {
        if (processFile.Path.EndsWith("GitleaksSecurityScanner.cs", StringComparison.Ordinal))
        {
            return context.Sources
                .Where(file => file.Content.Contains("SecurityScan", StringComparison.Ordinal) &&
                               file.Content.Contains("scanner.ScanAsync", StringComparison.Ordinal) &&
                               !file.Path.EndsWith("BoundaryInventorySensor.cs", StringComparison.Ordinal))
                .Select(file => new BoundarySourceLocation(file.Path,
                    Line(file.Content, file.Content.IndexOf("scanner.ScanAsync", StringComparison.Ordinal))))
                .ToArray();
        }
        return [];
    }

    private static string InferInputSource(string expression) =>
        expression.Contains("request", StringComparison.OrdinalIgnoreCase) ||
        expression.Contains("req.", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(expression, @"\b(root|range|relativePath|url|uri)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? "request"
            : expression.Contains("config", StringComparison.OrdinalIgnoreCase) ||
              expression.Contains("options", StringComparison.OrdinalIgnoreCase) ||
              expression.Contains("Path", StringComparison.Ordinal) ? "configuration"
            : expression.StartsWith('"') || expression.StartsWith('\'') ? "constant"
            : "unknown";

    private static async Task PersistAsync(
        string root,
        BoundaryInventory inventory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, InventoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(inventory, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string MethodText(string content, string name)
    {
        var match = Regex.Match(content,
            $@"(?m)^static\s+(?:async\s+)?[^\n=]+?\b{Regex.Escape(name)}\s*\(",
            RegexOptions.CultureInvariant);
        if (!match.Success) return string.Empty;
        var open = content.IndexOf('{', match.Index);
        var arrow = content.IndexOf("=>", match.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            var semicolon = content.IndexOf(';', arrow);
            return semicolon < 0 ? content[match.Index..] : content[match.Index..(semicolon + 1)];
        }
        if (open < 0) return string.Empty;
        var close = FindMatching(content, open, '{', '}');
        return close < 0 ? content[match.Index..] : content[match.Index..(close + 1)];
    }

    private static string MethodSignature(string content, string name)
    {
        var match = Regex.Match(content,
            $@"(?m)^static\s+(?:async\s+)?[^\n=]+?\b{Regex.Escape(name)}\s*\(",
            RegexOptions.CultureInvariant);
        if (!match.Success) return string.Empty;
        var open = content.IndexOf('(', match.Index);
        var close = FindMatching(content, open, '(', ')');
        return close < 0 ? match.Value : content[match.Index..(close + 1)];
    }

    private static string? HandlerName(string statement)
    {
        var match = Regex.Match(statement,
            @",\s*(?<handler>[A-Za-z_][A-Za-z0-9_]*)\s*\)(?:\.|;|$)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["handler"].Value : null;
    }

    private static string StatementAt(string content, int index)
    {
        var end = content.IndexOf(';', index);
        if (end < 0) end = Math.Min(content.Length - 1, index + 2000);
        return content[index..Math.Min(content.Length, end + 1)];
    }

    private static int FindMatching(string text, int open, char opening, char closing)
    {
        if (open < 0) return -1;
        var depth = 0;
        var quoted = false;
        var quote = '\0';
        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '\\') index++;
                else if (character == quote) quoted = false;
                continue;
            }
            if (character is '"' or '\'')
            {
                quoted = true;
                quote = character;
                continue;
            }
            if (character == opening) depth++;
            else if (character == closing && --depth == 0) return index;
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '(' or '<' or '[') depth++;
            else if (text[index] is ')' or '>' or ']') depth--;
            else if (text[index] == ',' && depth == 0)
            {
                result.Add(text[start..index]);
                start = index + 1;
            }
        }
        result.Add(text[start..]);
        return result;
    }

    private static string NormalizeRoute(string route)
    {
        var normalized = "/" + route.Trim().Trim('/');
        return normalized == "/" ? normalized : Regex.Replace(normalized, "/+", "/");
    }

    private static int Line(string content, int index) =>
        index < 0 ? 1 : 1 + content.AsSpan(0, Math.Min(index, content.Length)).Count('\n');

    private static bool IsLoopback(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static bool IsHostBinding(string content, int index)
    {
        var start = Math.Max(0, index - 160);
        var end = Math.Min(content.Length, index + 160);
        var context = content[start..end];
        return context.Contains("--urls", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("UseUrls", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("applicationUrl", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("\"urls\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJavaScript(SourceFile file) =>
        Path.GetExtension(file.Path) is ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx";

    private static string StableId(params string[] values) =>
        string.Join(':', values.Select(value => value.Trim().ToLowerInvariant()
            .Replace(' ', '-').Replace('/', ':')));

    private static string Hash(string material) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record SourceFile(string Path, string Content)
    {
        public IEnumerable<(int Line, string Text)> Lines()
        {
            var lines = Content.Split('\n');
            for (var index = 0; index < lines.Length; index++) yield return (index + 1, lines[index]);
        }
    }

    private sealed record AnalysisContext(string Root, IReadOnlyList<SourceFile> Sources);

    private sealed record RouteGroup(string Prefix, bool Authorized);

    [GeneratedRegex(@"\b(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\.Map(?<method>Get|Post|Put|Delete|Patch|Options|Head|Methods|Fallback|)\s*\(\s*""(?<route>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AspNetMapRegex();

    [GeneratedRegex(@"\bvar\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*[A-Za-z_][A-Za-z0-9_]*\.MapGroup\s*\(\s*""(?<prefix>[^""]*)""\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MapGroupRegex();

    [GeneratedRegex(@"(?<attributes>(?:\s*\[[^\]]+\]\s*)+)(?:(?:public|internal|sealed|abstract|partial)\s+)*class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*Controller)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ControllerRegex();

    [GeneratedRegex(@"\bRoute\s*\(\s*""(?<route>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ControllerRouteRegex();

    [GeneratedRegex(@"(?<attributes>(?:\s*\[[^\]]+\]\s*)*\s*\[Http(?<verb>Get|Post|Put|Delete|Patch)(?:\s*\(\s*""(?<route>[^""]*)""\s*\))?\](?:\s*\[[^\]]+\]\s*)*)\s*(?:public|internal|protected)\s+(?:async\s+)?(?<return>[A-Za-z_][A-Za-z0-9_<>,.?\[\]\s]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ControllerActionRegex();

    [GeneratedRegex(@"\b(?<operation>UseStaticFiles|MapFallbackToFile|MapHealthChecks|MapHub|UseWebSockets)\s*(?:<[^>]+>)?\s*\(\s*(?<argument>""[^""]*"")?", RegexOptions.CultureInvariant)]
    private static partial Regex SpecialAspNetRegex();

    [GeneratedRegex(@"AddHostedService(?:<(?<service>[^>]+)>|\s*\(\s*[^=]+=>\s*[^.]+\.GetRequiredService<(?<service>[^>]+)>)", RegexOptions.CultureInvariant)]
    private static partial Regex HostedServiceRegex();

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\?|\:[^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteParameterRegex();

    [GeneratedRegex(@"RequireRateLimiting\s*\(\s*""(?<policy>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex RequireRateRegex();

    [GeneratedRegex(@"\b(?<receiver>[A-Za-z_$][A-Za-z0-9_$]*)\.(?<method>get|post|put|delete|patch|all)\s*\(\s*['""`](?<route>/[^'""`]*)['""`]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NodeRouteRegex();

    [GeneratedRegex(@"(?:(?<receive>window\.addEventListener\s*\(\s*['""]message['""]|window\.onmessage\s*=)|postMessage\s*\([^\n]*,\s*['""](?<target>[^'""]+)['""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrowserMessageRegex();

    [GeneratedRegex(@"\b(?<operation>(?:cron\.)?schedule|(?:fs\.)?watch|chokidar\.watch|\.consume|\.on\s*\(\s*['""]message['""])\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptTriggerRegex();

    [GeneratedRegex(@"(?<operation>express\.static|\.on\s*\(\s*['""]connection['""]|new\s+(?:WebSocket\.)?Server)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptNetworkSurfaceRegex();

    [GeneratedRegex(@"<iframe\b[^>]*\bsrc\s*=\s*['""](?<target>[^'""]+)['""][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IframeRegex();

    [GeneratedRegex(@"(?:frame-ancestors|X-Frame-Options)\s*(?:[=:]\s*|['""]\s*,\s*['""])(?<value>[^;,'""\r\n}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FramePolicyRegex();

    [GeneratedRegex(@"(?<url>https?://(?:127\.0\.0\.1|localhost|\[::1\]|0\.0\.0\.0|\+|[A-Za-z_$][A-Za-z0-9_.$-]*)(?::(?:\d+|\$\{[^}]+\}))?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlBindingRegex();

    [GeneratedRegex(@"\.listen\s*\(\s*(?<port>[^,\)\n]+)(?:,\s*['""](?<host>[^'""]+)['""])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListenRegex();

    [GeneratedRegex(@"(?:AllowAnyOrigin\s*\(\)|WithOrigins\s*\((?<value>[^)]*)\)|[""']AllowedOrigins[""']\s*:\s*\[(?<value>[^\]]*)\]|\borigin\s*:\s*['""](?<value>[^'""]+)['""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorsRegex();

    [GeneratedRegex(@"(?:new\s+ProcessStartInfo\s*\(\s*|Process\.Start\s*\(\s*|(?:spawn|exec|execFile)\s*\(\s*)(?<executable>[^,\)\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessRegex();

    [GeneratedRegex(@"\.ArgumentList\.Add\s*\(\s*(?<argument>[^\)\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessArgumentRegex();

    [GeneratedRegex(@"(?:\b(?:httpClient|_httpClient)\.(?:GetAsync|PostAsync|SendAsync|PutAsync|DeleteAsync)\s*\(\s*|\bfetch\s*\(\s*|\baxios\.(?:get|post|put|delete|patch)\s*\(\s*)(?<target>[^,\)\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutboundRegex();

    [GeneratedRegex(@"\bnew\s+FileSystemWatcher\s*\(|\b(?:fs\.)?watch\s*\(|\bchokidar\.watch\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileWatcherRegex();

    [GeneratedRegex(@"(?:Results\.Problem|Problem\s*\(|res\.(?:send|json|status))[\s\S]{0,500}?(?:exception|error|err)\s*(?:\?*\.)\s*(?:Message|StackTrace|stack|message)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionDetailRegex();
}
