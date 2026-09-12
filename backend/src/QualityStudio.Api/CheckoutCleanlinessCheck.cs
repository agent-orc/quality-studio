using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Reports at startup whether a registered project root still carries studio data in its Git
/// checkout. The studio writes to a data root outside the checkout now, so a finding here is data
/// from before that change or a foreign writer - and either way it is what makes an Agent Studio
/// integration of that checkout fail.
/// <para>
/// It warns and never refuses to start: the host serves every other repository perfectly well, and
/// the operator decides when to migrate and commit. It runs in the background rather than in
/// <c>StartAsync</c> because it starts a <c>git status</c> per registered repository, and a large
/// working copy would hold the listener closed for as long as that takes.
/// </para>
/// </summary>
public sealed class CheckoutCleanlinessCheck : BackgroundService
{
    private readonly RepositoryRegistry registry;
    private readonly ILogger<CheckoutCleanlinessCheck> logger;

    public CheckoutCleanlinessCheck(RepositoryRegistry registry, ILogger<CheckoutCleanlinessCheck> logger)
    {
        this.registry = registry;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var registration in registry.List())
        {
            if (stoppingToken.IsCancellationRequested) return;
            CheckoutQualityStatus status;
            try
            {
                status = await CheckoutQualityInspector
                    .InspectAsync(registration.RootPath, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                logger.LogDebug(exception, "Could not inspect the .quality tree of {Repository}.", registration.Id);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!status.NeedsAttention) continue;
            logger.LogWarning(
                new EventId(1620, "CheckoutCarriesQualityData"),
                "Repository '{Repository}' at {Root}: {Reason}",
                registration.Id, registration.RootPath, status.Describe());
        }
    }
}
