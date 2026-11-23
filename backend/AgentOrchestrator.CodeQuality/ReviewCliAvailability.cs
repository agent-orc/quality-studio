using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Local CLI evidence joined to the policy by the review host; it never changes prices.</summary>
public sealed record ReviewCliAvailability(
    IReadOnlySet<string> CodexModels, Version? CodexVersion, Version? ClaudeVersion)
{
    public bool Allows(string modelId, string cli, string? minimumVersion)
    {
        if (cli == "codex" && !CodexModels.Contains(modelId)) return false;

        if (minimumVersion is null) return true;
        var installed = cli switch { "codex" => CodexVersion, "claude" => ClaudeVersion, _ => null };
        return Version.TryParse(minimumVersion, out var minimum) && installed >= minimum;
    }

    /// <summary>Read-only, bounded discovery. Missing or failed probes supply no support evidence.</summary>
    public static async Task<ReviewCliAvailability> DiscoverAsync(
        ISensorCommandRunner? runner = null, CancellationToken cancellationToken = default)
    {
        runner ??= new ProcessSensorCommandRunner(TimeSpan.FromSeconds(10));
        var outputs = await Task.WhenAll(
            ProbeAsync("codex", ["debug", "models"]),
            ProbeAsync("codex", ["--version"]),
            ProbeAsync("claude", ["--version"])).ConfigureAwait(false);
        return FromOutput(outputs[0], outputs[1], outputs[2]);

        async Task<string?> ProbeAsync(string executable, string[] arguments)
        {
            try
            {
                var result = await runner.RunAsync(executable, arguments, Path.GetTempPath(), cancellationToken)
                    .ConfigureAwait(false);
                return result.ExitCode == 0 ? result.StandardOutput : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Discovery is optional evidence: launch, permission, timeout, and runner failures
                // must not stop the host. Caller cancellation above still ends discovery.
                return null;
            }
        }
    }

    public static ReviewCliAvailability FromOutput(string? codexModels, string? codexVersion, string? claudeVersion)
    {
        var models = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(codexModels ?? "{}");
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("models", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String &&
                        row.TryGetProperty("visibility", out var visibility) && visibility.ValueKind == JsonValueKind.String &&
                        visibility.GetString() == "list") models.Add(slug.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed response is absence of evidence, not permission to offer the model.
        }
        return new ReviewCliAvailability(models, ParseVersion(codexVersion), ParseVersion(claudeVersion));
    }

    private static Version? ParseVersion(string? output)
    {
        foreach (var word in (output ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Version.TryParse(word, out var version)) return version;
        }
        return null;
    }
}
