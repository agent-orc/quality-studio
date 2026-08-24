using System.Reflection;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Route facts recorded alongside every observation. A value that the runner cannot report stays
/// <see cref="Unknown"/>: a thinking level is never inferred from a model name, and a missing
/// provider is never backfilled from today's routing policy.
/// </summary>
public static class ReviewRouteProvenance
{
    public const string Unknown = "unknown";
    private const string RoutingResource =
        "AgentOrchestrator.CodeQuality.catalogues.token-economy-model-routing-policy.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ProvidersByCli =
        new(LoadProvidersByCli, isThreadSafe: true);

    /// <summary>The version of the routing policy that was in force when the run was recorded.</summary>
    public static string PolicyVersion => ReviewModelCatalog.Default.Snapshot.PolicyVersion;

    /// <summary>The provider that owns a CLI, or <see cref="Unknown"/> when the policy does not name one.</summary>
    public static string ProviderForCli(string? cliType) =>
        string.IsNullOrWhiteSpace(cliType)
            ? Unknown
            : ProvidersByCli.Value.GetValueOrDefault(ReviewModelCatalog.NormalizeCli(cliType), Unknown);

    /// <summary>Normalizes a value the runner could not report into the explicit unknown term.</summary>
    public static string OrUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim();

    private static IReadOnlyDictionary<string, string> LoadProvidersByCli()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(RoutingResource)
            ?? throw new InvalidOperationException($"Embedded routing policy '{RoutingResource}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in document.RootElement.GetProperty("providers").EnumerateArray())
        {
            var cli = ReviewModelCatalog.NormalizeCli(provider.GetProperty("cliId").GetString());
            result[cli] = provider.GetProperty("id").GetString()!;
        }

        return result;
    }
}
