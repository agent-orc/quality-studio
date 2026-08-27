using QualityStudio.Api;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class LocalBindingGuardTests
{
    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("http://localhost:5000;https://127.0.0.1:5001")]
    public void Loopback_urls_are_accepted_in_local_mode(string urls) =>
        LocalBindingGuard.EnsureLoopbackWhenLocal(isLocal: true, urls);

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://*:5000")]
    [InlineData("http://+:5000")]
    [InlineData("http://192.168.1.20:5000")]
    [InlineData("http://localhost:5000;http://0.0.0.0:5001")]
    public void Non_loopback_urls_fail_startup_in_local_mode(string urls)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => LocalBindingGuard.EnsureLoopbackWhenLocal(isLocal: true, urls));
        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_loopback_urls_are_ignored_outside_local_mode() =>
        LocalBindingGuard.EnsureLoopbackWhenLocal(isLocal: false, "http://0.0.0.0:5000");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_configuration_does_not_fail_local_mode_startup(string? urls) =>
        LocalBindingGuard.EnsureLoopbackWhenLocal(isLocal: true, urls);
}
