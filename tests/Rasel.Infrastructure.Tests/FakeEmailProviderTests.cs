using Rasel.Infrastructure.Providers;

namespace Rasel.Infrastructure.Tests;

public sealed class FakeEmailProviderTests
{
    [Fact]
    public async Task SendAsync_RecordsEmailForDevelopment()
    {
        var provider = new FakeEmailProvider();

        await provider.SendAsync("customer@example.com", "Welcome", "<p>Hello</p>", true, CancellationToken.None);

        var sent = Assert.Single(provider.SentMessages);
        Assert.Equal("customer@example.com", sent.To);
        Assert.Equal("Welcome", sent.Subject);
        Assert.Equal("<p>Hello</p>", sent.Body);
        Assert.True(sent.IsHtml);
    }

    [Fact]
    public async Task SendAsync_ThrowsWhenConfiguredToFail()
    {
        var provider = new FakeEmailProvider { FailSends = true };

        await Assert.ThrowsAsync<Rasel.Application.Ports.ProviderException>(
            () => provider.SendAsync("customer@example.com", "Subject", "Body", false, CancellationToken.None));
    }
}
