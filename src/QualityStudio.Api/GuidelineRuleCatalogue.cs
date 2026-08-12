using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Compatibility catalogue for the current Guidelines UI. Rule-library content is
/// host-side input and is deliberately not compiled into the analysis-core package.
/// </summary>
public static class GuidelineRuleCatalogue
{
    public static IReadOnlyList<GuidelineCatalogueEntry> Entries { get; } =
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

    public static GuidelineDefinition Install(GuidelineStore store, string repositoryRoot, string catalogueId)
    {
        var entry = Entries.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.Id, catalogueId))
            ?? throw new KeyNotFoundException($"Catalogue guideline '{catalogueId}' was not found.");
        return store.Create(repositoryRoot, entry.Guideline);
    }

    private static GuidelineCatalogueEntry Entry(
        string id,
        string title,
        string technology,
        string description,
        string kind,
        int priority,
        string content) =>
        new(id, title, technology, description, new GuidelineDraft(id, true, priority, [kind], ["file"], content));
}
