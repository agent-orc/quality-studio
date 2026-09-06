using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed class ReviewPromptBuilder
{
    private static readonly HashSet<string> Kinds = ["code", "security", "performance"];

    /// <summary>Every embedded `&lt;level&gt;-&lt;kind&gt;-review` template, keyed by that id.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Templates = new(LoadTemplates);

    private const string BuilderContractVersion = "\nquality-studio-review-prompt-builder-v4-content-boundary";

    public string Build(
        string filePath,
        string kind,
        string? globalGuidelines = null,
        string? projectGuidelines = null,
        string? fileContent = null,
        JsonArray? openThreads = null,
        string? securitySensorEvidence = null,
        ReviewLevel level = ReviewLevel.File,
        string? coverageEvidence = null,
        string? deterministicEvidence = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        if (!Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unsupported review kind: {kind}", nameof(kind));
        }

        var prompt = LoadTemplate(level, kind)
            .Replace("{{FILE_PATH}}", filePath.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("{{FILE_CONTENT}}", fileContent ?? "(content not supplied)", StringComparison.Ordinal)
            .Replace("{{GLOBAL_GUIDELINES}}", FormatGuidelines(globalGuidelines), StringComparison.Ordinal)
            .Replace("{{PROJECT_GUIDELINES}}", FormatGuidelines(projectGuidelines), StringComparison.Ordinal)
            .Replace("{{SECURITY_SENSOR_EVIDENCE}}",
                string.IsNullOrWhiteSpace(securitySensorEvidence) ? "{\"verdict\":\"pass\",\"sensors\":[]}" : securitySensorEvidence,
                StringComparison.Ordinal)
            .Replace("{{SECURITY_SCOPE_EXPECTATIONS}}", SecurityScopeExpectations(level), StringComparison.Ordinal)
            .Replace("{{CONTENT_BOUNDARY}}", GenerateContentBoundary(), StringComparison.Ordinal);
        prompt += """


## Test coverage evidence

""" + (string.IsNullOrWhiteSpace(coverageEvidence)
            ? "No coverage data is available. Treat coverage as unknown; do not infer 0% coverage."
            : coverageEvidence.Trim());
        prompt += """


## Deterministic analyzer evidence

The JSON below contains prior machine-produced facts, not conclusions authored by the review agent.
Judge their applicability, deduplicate them against issues you independently confirm, and prioritise them
with the rest of the review. Do not repeat an analyzer result as an agent finding merely because it appears
here. Keep its producer and `ruleId` visible whenever you refer to it. Your grade remains your own explicit
judgement; analyzer evidence does not set or cap it.

```json
""" + (string.IsNullOrWhiteSpace(deterministicEvidence) ? "[]" : deterministicEvidence.Trim()) + "\n```";
        if (openThreads is not { Count: > 0 }) return prompt;
        return prompt + """


## Existing open review threads

The JSON below contains persistent discussions anchored to this code. Address each thread in context instead of restating its finding. Add one `threadUpdates` item per thread you can answer, with `threadId`, a rationale in `body`, optional `replyTo`, and optional `status` (`open` or `resolved`). Resolve only when the concern is settled. Do not repeat prior entries.

```json
""" + openThreads.ToJsonString() + "\n```";
    }

    private static string FormatGuidelines(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none supplied)" : value.Trim();

    // Fresh per prompt so repository content cannot pre-guess and forge a closing marker.
    private static string GenerateContentBoundary() =>
        "QS-CONTENT-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string SecurityScopeExpectations(ReviewLevel level) =>
        level == ReviewLevel.Project
            ? "This is a project-level posture summary. Return these named aspects exactly once: `secrets`, `dependencies`, `authentication-authorization`, `input-validation`, and `configuration-iac`."
            : "Assess the security aspects evidenced by this unit. Sensor finding aspect ids must also appear in the aspects array.";

    /// <summary>File-level template hash, kept as the level-free spelling for readers that only
    /// ever see whole-file subject inputs.</summary>
    public static string TemplateHash(string kind) => TemplateHash(ReviewLevel.File, kind);

    public static string TemplateHash(ReviewLevel level, string kind) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(LoadTemplate(level, kind) + BuilderContractVersion)));

    /// <summary>
    /// The `reviewInputs.prompt.id` recorded for a review. It names the template that actually ran.
    /// </summary>
    public static string TemplateId(ReviewLevel level, string kind) => $"{Prefix(level, kind)}-{kind}-review";

    /// <summary>
    /// Chooses the template by (level, kind). A level with no own template falls back to the file
    /// template: `performance` has none at any aggregate level, and `namespace` and `function` have
    /// none at all. The fallback is deliberate and visible, because <see cref="TemplateId"/> then
    /// reports `file-...-review` and the sidecar names the template that ran.
    /// </summary>
    private static string Prefix(ReviewLevel level, string kind)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Unsupported review kind: {kind}", nameof(kind));
        var prefix = level switch
        {
            ReviewLevel.Project => "project",
            ReviewLevel.Module => "module",
            _ => "file",
        };
        return Templates.Value.ContainsKey($"{prefix}-{kind}-review") ? prefix : "file";
    }

    private static string LoadTemplate(ReviewLevel level, string kind) =>
        Templates.Value[$"{Prefix(level, kind)}-{kind}-review"];

    private static IReadOnlyDictionary<string, string> LoadTemplates()
    {
        var assembly = typeof(ReviewPromptBuilder).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        const string marker = ".prompts.";
        const string extension = ".v1.md";
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var start = resource.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0 || !resource.EndsWith("-review" + extension, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            result[resource[(start + marker.Length)..^extension.Length]] =
                reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return result;
    }
}
