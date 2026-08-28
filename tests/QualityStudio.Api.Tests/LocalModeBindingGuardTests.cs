using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class LocalModeBindingGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("https://127.0.0.1:5001")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://LOCALHOST:5000")]
    [InlineData("http://[::1]:5000")]
    public void IsLoopbackAddress_accepts_loopback_hosts(string address) =>
        Assert.True(LocalModeBindingGuard.IsLoopbackAddress(address));

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://10.0.0.5:5000")]
    [InlineData("http://192.168.1.20:5000")]
    [InlineData("http://[::]:5000")]
    [InlineData("not-a-url")]
    public void IsLoopbackAddress_rejects_non_loopback_or_unparsable_hosts(string address) =>
        Assert.False(LocalModeBindingGuard.IsLoopbackAddress(address));

    [Fact]
    public void FindNonLoopbackAddresses_returns_only_the_offending_entries()
    {
        var addresses = new[] { "http://127.0.0.1:5000", "http://0.0.0.0:5001", "https://localhost:5002" };

        var nonLoopback = LocalModeBindingGuard.FindNonLoopbackAddresses(addresses);

        Assert.Equal(["http://0.0.0.0:5001"], nonLoopback);
    }

    [Fact]
    public async Task EnforceLoopbackBinding_stops_the_app_when_local_mode_binds_a_non_loopback_address()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        await using var app = builder.Build();

        var exitCodes = new List<int>();
        LocalModeBindingGuard.EnforceLoopbackBinding(app, isLocal: true, setExitCode: exitCodes.Add);

        var runTask = app.RunAsync();
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(runTask, completed);
        await runTask;
        Assert.Equal([1], exitCodes);
    }

    [Fact]
    public async Task EnforceLoopbackBinding_leaves_a_loopback_bound_local_mode_app_running()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        var exitCodes = new List<int>();
        LocalModeBindingGuard.EnforceLoopbackBinding(app, isLocal: true, setExitCode: exitCodes.Add);

        await app.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Empty(exitCodes);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnforceLoopbackBinding_does_nothing_when_not_in_local_mode()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        await using var app = builder.Build();

        var exitCodes = new List<int>();
        LocalModeBindingGuard.EnforceLoopbackBinding(app, isLocal: false, setExitCode: exitCodes.Add);

        await app.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Empty(exitCodes);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
