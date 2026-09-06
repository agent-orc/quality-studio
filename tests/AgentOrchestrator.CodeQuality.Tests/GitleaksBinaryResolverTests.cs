using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class GitleaksBinaryResolverTests
{
    [Fact]
    public void The_tracked_digest_table_is_well_formed_and_covers_the_platforms_this_product_ships_on()
    {
        var digests = GitleaksBinaryResolver.PinnedArchiveDigestTable;

        Assert.NotEmpty(digests);
        foreach (var (platform, digest) in digests)
        {
            Assert.Matches("^[a-z0-9]+_[a-z0-9]+$", platform);
            Assert.Equal(64, digest.Length);
            Assert.All(digest, character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        }

        // The container image runs linux-x64; development happens on windows-x64 and darwin-arm64.
        Assert.Contains("linux_x64", digests.Keys);
        Assert.Contains("windows_x64", digests.Keys);
        Assert.Contains("darwin_arm64", digests.Keys);
    }

    [Fact]
    public void Every_tracked_digest_is_distinct_so_a_copy_paste_slip_cannot_pass_verification()
    {
        var digests = GitleaksBinaryResolver.PinnedArchiveDigestTable;

        Assert.Equal(digests.Count, digests.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task An_explicit_offline_path_that_does_not_exist_is_refused_by_name()
    {
        var resolver = new GitleaksBinaryResolver(cacheDirectory: Path.Combine(Path.GetTempPath(),
            "quality-studio-gitleaks-" + Guid.NewGuid().ToString("N")));
        var missing = Path.Combine(Path.GetTempPath(), "no-gitleaks-here", "gitleaks");

        var refused = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() =>
            resolver.ResolveAsync(missing, TestContext.Current.CancellationToken));

        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
    }
}
