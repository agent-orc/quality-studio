using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// QS-102's acceptance: the studio derives data about a checkout without dirtying it. Agent Studio
/// refuses to fast-forward an integration checkout with uncommitted changes, so every derived write
/// that lands in the tree is a delivery someone cannot ship.
/// </summary>
public sealed class CheckoutStaysCleanTests
{
    [Fact]
    public async Task Deriving_data_about_a_checkout_leaves_its_tree_untouched()
    {
        using var checkout = TemporaryDirectory.Create("quality-clean-checkout-");
        await File.WriteAllTextAsync(Path.Combine(checkout.Path, "Program.cs"), """
            var app = WebApplication.Create();
            app.MapGet("/orders", () => Results.Ok());
            app.Run();
            """, TestContext.Current.CancellationToken);
        var before = Snapshot(checkout.Path);

        await new BoundaryInventorySensor().RunAsync(
            new SensorScanRequest(checkout.Path), TestContext.Current.CancellationToken);
        await UsageLedger.AppendAsync(checkout.Path, SampleUsage(), TestContext.Current.CancellationToken);
        await CoverageSnapshot.Empty("2026-09-07T00:00:00Z", "commit")
            .SaveAsync(checkout.Path, TestContext.Current.CancellationToken);
        new QualityRunReportPinStore(checkout.Path).Pin("review-1");

        Assert.Equal(before, Snapshot(checkout.Path));
        Assert.False(Directory.Exists(Path.Combine(checkout.Path, ".quality")),
            "a derived write created a .quality directory inside the analysed checkout.");
    }

    [Fact]
    public async Task The_derived_data_is_readable_back_from_the_data_root()
    {
        using var checkout = TemporaryDirectory.Create("quality-clean-roundtrip-");

        await UsageLedger.AppendAsync(checkout.Path, SampleUsage(), TestContext.Current.CancellationToken);
        await CoverageSnapshot.Empty("2026-09-07T00:00:00Z", "commit")
            .SaveAsync(checkout.Path, TestContext.Current.CancellationToken);

        // Relocating the write is only half the contract; a reader that still looked in the checkout
        // would report an empty ledger rather than fail, which is the harder failure to notice.
        var usage = await UsageLedger.QueryAsync(checkout.Path,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, usage.Runs);
        Assert.NotNull(CoverageSnapshot.Load(checkout.Path));
    }

    private static ReviewUsageEntry SampleUsage() => new(
        "cli-run-1", DateTimeOffset.Parse("2026-09-07T00:00:00Z"), "test-model", "codex",
        new TokenUsage(50, 10, 20, 2, 600), "code", "file", "src/Program.cs",
        null, UsageLedger.CurrentSchemaVersion, ReviewModelSource.PolicyDefault);

    /// <summary>Every file below the checkout with its length, so a new or rewritten file shows up.</summary>
    private static IReadOnlyList<string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{new FileInfo(path).Length}")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
