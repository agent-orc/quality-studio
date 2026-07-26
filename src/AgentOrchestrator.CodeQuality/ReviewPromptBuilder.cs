using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed class ReviewPromptBuilder
{
    private static readonly HashSet<string> Kinds = ["code", "security", "performance"];

    public string Build(
        string filePath,
        string kind,
        string? globalGuidelines = null,
        string? projectGuidelines = null,
        string? fileContent = null,
        JsonArray? openThreads = null,
        string? securitySensorEvidence = null,
        ReviewLevel level = ReviewLevel.File)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        if (!Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unsupported review kind: {kind}", nameof(kind));
        }

        var prompt = LoadTemplate(kind)
            .Replace("{{FILE_PATH}}", filePath.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("{{FILE_CONTENT}}", fileContent ?? "(content not supplied)", StringComparison.Ordinal)
            .Replace("{{GLOBAL_GUIDELINES}}", FormatGuidelines(globalGuidelines), StringComparison.Ordinal)
            .Replace("{{PROJECT_GUIDELINES}}", FormatGuidelines(projectGuidelines), StringComparison.Ordinal)
            .Replace("{{SECURITY_SENSOR_EVIDENCE}}",
                string.IsNullOrWhiteSpace(securitySensorEvidence) ? "{\"verdict\":\"pass\",\"sensors\":[]}" : securitySensorEvidence,
                StringComparison.Ordinal)
            .Replace("{{SECURITY_SCOPE_EXPECTATIONS}}", SecurityScopeExpectations(level), StringComparison.Ordinal);
        if (openThreads is not { Count: > 0 }) return prompt;
        return prompt + """


## Existing open review threads

The JSON below contains persistent discussions anchored to this code. Address each thread in context instead of restating its finding. Add one `threadUpdates` item per thread you can answer, with `threadId`, a rationale in `body`, optional `replyTo`, and optional `status` (`open` or `resolved`). Resolve only when the concern is settled. Do not repeat prior entries.

```json
""" + openThreads.ToJsonString() + "\n```";
    }

    private static string FormatGuidelines(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none supplied)" : value.Trim();

    private static string SecurityScopeExpectations(ReviewLevel level) =>
        level == ReviewLevel.Project
            ? "This is a project-level posture summary. Return these named aspects exactly once: `secrets`, `dependencies`, `authentication-authorization`, `input-validation`, and `configuration-iac`."
            : "Assess the security aspects evidenced by this unit. Sensor finding aspect ids must also appear in the aspects array.";

    public static string TemplateHash(string kind) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LoadTemplate(kind))));

    private static string LoadTemplate(string kind)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Unsupported review kind: {kind}", nameof(kind));
        var suffix = $"prompts.file-{kind}-review.v1.md";
        var assembly = typeof(ReviewPromptBuilder).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
