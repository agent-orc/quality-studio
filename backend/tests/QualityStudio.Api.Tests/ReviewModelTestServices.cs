using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QualityStudio.Api.Tests;

internal static class ReviewModelTestServices
{
    // Fake-executor tests need stable CLI evidence. Discovery and rejection have dedicated tests.
    public static void UseSupportedReviewModels(this IServiceCollection services)
    {
        var models = ReviewModelCatalog.Default.Snapshot.Models.Where(model => model.CliType == "codex")
            .Select(model => new { slug = model.ModelId, visibility = "list" });
        var evidence = ReviewCliAvailability.FromOutput(
            JsonSerializer.Serialize(new { models }), "codex-cli 0.166.0", "2.1.305 (Claude Code)");
        services.RemoveAll<ReviewModelCatalog>();
        services.AddSingleton(new ReviewModelCatalog(evidence));
    }
}
