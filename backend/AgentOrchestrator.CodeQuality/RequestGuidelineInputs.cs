using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Context supplied by a host for one request, independent of the current repository guidelines.
/// Its digest is retained so a scan can verify repository policy in that recorded context; a new
/// review request always supplies its own context and therefore detects additions, changes and removal.
/// </summary>
internal static class RequestGuidelineInputs
{
    private const string Version = "request-guidelines-v1";
    private const string GlobalId = "qs-request-global-guidelines";
    private const string ProjectId = "qs-request-project-guidelines";

    public static IReadOnlyList<StandardReference> ForRequest(string? global, string? project)
    {
        var result = new List<StandardReference>(2);
        Add(result, GlobalId, StandardScope.Global, global);
        Add(result, ProjectId, StandardScope.Project, project);
        return result;
    }

    public static IReadOnlyList<StandardReference> FromStored(ReviewInputs inputs) =>
        (inputs.Standards ?? []).Where(input => input is not null && input.Version == Version &&
            ((input.Id == GlobalId && input.Scope == StandardScope.Global) ||
             (input.Id == ProjectId && input.Scope == StandardScope.Project)))
            .OrderBy(input => input.Id, StringComparer.Ordinal).ToArray();

    private static void Add(List<StandardReference> result, string id, StandardScope scope, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        // Combine() trims request guidelines before putting them in the prompt.
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.Trim())));
        result.Add(new StandardReference(id, scope, Version, digest));
    }
}
