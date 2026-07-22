using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record GuidelineDefinition(
    string Id,
    string FileName,
    bool Enabled,
    int Priority,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Levels,
    string Content);

public sealed record GuidelineDraft(
    string Id,
    bool Enabled,
    int Priority,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Levels,
    string Content);

public sealed record GuidelineCatalogueEntry(
    string Id,
    string Title,
    string Technology,
    string Description,
    GuidelineDraft Guideline);

public sealed partial class GuidelineStore
{
    private static readonly HashSet<string> ReviewKinds = ["code", "security", "performance", "all", "*"];
    private static readonly HashSet<string> ReviewLevels = Enum.GetNames<ReviewLevel>()
        .Select(value => value.ToLowerInvariant()).Append("all").Append("*").ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<GuidelineCatalogueEntry> Catalogue { get; } =
    [
        Entry("dotnet-api-safety", ".NET API safety", ".NET", "Cancellation, disposal, async and public API guidance", "code", 80,
            "Prefer async APIs for I/O, propagate CancellationToken, dispose owned resources, and validate arguments at public boundaries. Report a finding only when the concrete code violates one of these rules."),
        Entry("angular-typescript", "Angular and TypeScript", "Angular / TypeScript", "Typed templates, signals, subscriptions and browser safety", "code", 75,
            "Keep TypeScript strictly typed, avoid manual subscriptions when declarative Angular primitives work, preserve accessible semantics, and never bypass framework sanitization without a documented trust boundary."),
        Entry("testing-confidence", "Testing confidence", "Testing", "Deterministic behavior-focused test guidance", "code", 70,
            "Tests should assert observable behavior, cover failure and cancellation paths, avoid timing-dependent waits, and keep fixtures isolated and deterministic. Do not request tests for trivial forwarding code without meaningful behavior."),
        Entry("security-boundaries", "Security boundaries", "Security", "Input, secret, authorization and logging guidance", "security", 100,
            "Validate untrusted input at its boundary, enforce authorization server-side, keep secrets out of source and logs, use parameterized data access, and avoid exposing sensitive values in errors or telemetry."),
    ];

    public IReadOnlyList<GuidelineDefinition> List(string repositoryRoot)
    {
        var directory = DirectoryPath(repositoryRoot);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => InputResolver.ParseFile(path))
            .Select(input => new GuidelineDefinition(input.Id, Path.GetFileName(input.Source), input.Enabled,
                input.Priority, input.Kinds, input.Levels, input.Content))
            .OrderByDescending(value => value.Priority)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public GuidelineDefinition Create(string repositoryRoot, GuidelineDraft draft)
    {
        Validate(draft);
        if (List(repositoryRoot).Any(value => StringComparer.OrdinalIgnoreCase.Equals(value.Id, draft.Id)))
            throw new ArgumentException($"A guideline with id '{draft.Id}' already exists.");
        return Write(repositoryRoot, draft, FileName(draft.Id));
    }

    public GuidelineDefinition Update(string repositoryRoot, string existingId, GuidelineDraft draft)
    {
        Validate(draft);
        var existing = List(repositoryRoot).SingleOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Id, existingId))
            ?? throw new KeyNotFoundException($"Guideline '{existingId}' was not found.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(existingId, draft.Id) &&
            List(repositoryRoot).Any(value => StringComparer.OrdinalIgnoreCase.Equals(value.Id, draft.Id)))
            throw new ArgumentException($"A guideline with id '{draft.Id}' already exists.");
        var result = Write(repositoryRoot, draft, existing.FileName);
        return result;
    }

    public void Delete(string repositoryRoot, string id)
    {
        var existing = List(repositoryRoot).SingleOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Id, id))
            ?? throw new KeyNotFoundException($"Guideline '{id}' was not found.");
        File.Delete(Path.Combine(DirectoryPath(repositoryRoot), existing.FileName));
    }

    public GuidelineDefinition Install(string repositoryRoot, string catalogueId)
    {
        var entry = Catalogue.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.Id, catalogueId))
            ?? throw new KeyNotFoundException($"Catalogue guideline '{catalogueId}' was not found.");
        return Create(repositoryRoot, entry.Guideline);
    }

    public static string Serialize(GuidelineDraft draft)
    {
        Validate(draft);
        static string Values(IReadOnlyList<string> values) => $"[{string.Join(", ", values.Select(value => value.ToLowerInvariant()))}]";
        return $"---\nid: {draft.Id}\nenabled: {draft.Enabled.ToString().ToLowerInvariant()}\nkinds: {Values(draft.Kinds)}\nlevels: {Values(draft.Levels)}\npriority: {draft.Priority}\n---\n{draft.Content.Trim()}\n";
    }

    private static GuidelineDefinition Write(string repositoryRoot, GuidelineDraft draft, string fileName)
    {
        var directory = DirectoryPath(repositoryRoot);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, Serialize(draft), new UTF8Encoding(false));
        File.Move(temporary, path, true);
        return new GuidelineDefinition(draft.Id, fileName, draft.Enabled, draft.Priority,
            draft.Kinds.Select(value => value.ToLowerInvariant()).ToArray(),
            draft.Levels.Select(value => value.ToLowerInvariant()).ToArray(), draft.Content.Trim());
    }

    private static string DirectoryPath(string repositoryRoot) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "inputs");

    private static string FileName(string id) => id + ".md";

    private static GuidelineCatalogueEntry Entry(string id, string title, string technology, string description,
        string kind, int priority, string content) =>
        new(id, title, technology, description, new GuidelineDraft(id, true, priority, [kind], ["file"], content));

    private static void Validate(GuidelineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!IdPattern().IsMatch(draft.Id ?? string.Empty))
            throw new ArgumentException("Guideline id must be 2-128 lowercase letters, digits, dots, underscores or hyphens.");
        if (draft.Priority is < -100000 or > 100000) throw new ArgumentException("Guideline priority must be between -100000 and 100000.");
        if (draft.Kinds is null || draft.Kinds.Count == 0 || draft.Kinds.Any(value => !ReviewKinds.Contains(value.ToLowerInvariant())))
            throw new ArgumentException("A guideline requires supported review kinds.");
        if (draft.Levels is null || draft.Levels.Count == 0 || draft.Levels.Any(value => !ReviewLevels.Contains(value.ToLowerInvariant())))
            throw new ArgumentException("A guideline requires supported review levels.");
        if (string.IsNullOrWhiteSpace(draft.Content)) throw new ArgumentException("Guideline content is required.");
        if (draft.Content.Length > 100000) throw new ArgumentException("Guideline content cannot exceed 100,000 characters.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
