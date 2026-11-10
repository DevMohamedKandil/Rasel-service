using System.Net;
using Microsoft.Extensions.Options;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;
using Rasel.Infrastructure.Providers;

namespace Rasel.Infrastructure.Tests;

public sealed class TextBeeSmsProviderTests
{
    [Fact]
    public async Task SendAsync_SendsExpectedRequest()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.textbee.dev/api/v1/gateway/send-sms", request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var provider = CreateProvider(handler);

        await provider.SendAsync("+201001234567", "test message", CancellationToken.None);

        Assert.Contains("\"recipients\":[\"+201001234567\"]", handler.Body);
        Assert.Contains("\"message\":\"test message\"", handler.Body);
    }

    [Fact]
    public async Task SendAsync_IncludesConfiguredDeviceId()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(handler, new TextBeeOptions
        {
            ApiKey = "test-key",
            DeviceId = "device-123"
        });

        await provider.SendAsync("+201001234567", "test", CancellationToken.None);

        Assert.Contains("\"deviceId\":\"device-123\"", handler.Body);
    }

    [Fact]
    public async Task SendAsync_TreatsRequestTimeoutAsTransient()
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout))));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => provider.SendAsync("+201001234567", "test", CancellationToken.None));

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task SendAsync_TreatsQuotaLimitAsPermanent()
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => provider.SendAsync("+201001234567", "test", CancellationToken.None));

        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task SendAsync_TreatsServerErrorsAsTransient()
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => provider.SendAsync("+201001234567", "test", CancellationToken.None));

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task SendAsync_TreatsUnauthorizedAsPermanent()
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => provider.SendAsync("+201001234567", "test", CancellationToken.None));

        Assert.False(error.IsTransient);
    }

    private static TextBeeSmsProvider CreateProvider(StubHandler handler, TextBeeOptions? options = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(TextBeeOptions.DefaultBaseUrl) },
            Options.Create(options ?? new TextBeeOptions { ApiKey = "test-key" }));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return await send(request, ct);
        }
    }
}
