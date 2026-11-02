using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Rasel.Application.Ports;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Providers.WebJs;

namespace Rasel.Infrastructure.Tests;

public class WebJsProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return await respond(request, Interlocked.Increment(ref _calls), ct);
        }
    }

    private static IWhatsAppProvider Build(StubHandler stub, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["WhatsApp:Provider"] = "WebJs",
            ["WhatsApp:WebJs:BaseUrl"] = "http://sidecar.test",
            ["WhatsApp:WebJs:ApiToken"] = "token-123",
            ["WhatsApp:WebJs:ReadRetries"] = "2",
            ["WhatsApp:WebJs:BreakerMinimumThroughput"] = "2",
            ["WhatsApp:WebJs:AttemptTimeoutSeconds"] = "1",
        };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<Rasel.Infrastructure.Configuration.WhatsAppOptions>().Bind(config.GetSection("WhatsApp"));
        services.AddWebJsProvider();
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = stub));
        return services.BuildServiceProvider().GetRequiredService<IWhatsAppProvider>();
    }

    private static HttpResponseMessage Status(HttpStatusCode code, object? body = null) =>
        new(code) { Content = body is null ? null : JsonContent.Create(body) };

    [Fact]
    public async Task Maps_sidecar_responses_and_sends_auth_and_idempotency_headers()
    {
        var stub = new StubHandler((req, _, _) => Task.FromResult(req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/status") => Status(HttpStatusCode.OK, new { status = "Connected" }),
            var p when p.EndsWith("/qr") => Status(HttpStatusCode.OK, new { qr = "qr-data" }),
            var p when p.EndsWith("/groups") => Status(HttpStatusCode.OK, new[] { new { id = "1", name = "Ops" }, new { id = "2", name = "Ops team" } }),
            _ => Status(HttpStatusCode.OK),
        }));
        var provider = Build(stub);
        var session = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        Assert.Equal(SessionStatus.Connected, await provider.GetStatusAsync(session, default));
        Assert.Equal("qr-data", await provider.GetQrAsync(session, default));
        Assert.Equal("Ops", Assert.Single(await provider.FindGroupsByNameAsync(session, "Ops", default)).Name); // exact match only
        await provider.SendTextToPhoneAsync(session, "+201001234567", "hi", messageId, default);

        var send = stub.Requests.Last();
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal("Bearer token-123", send.Headers.Authorization!.ToString());
        Assert.Equal(messageId.ToString(), send.Headers.GetValues("Idempotency-Key").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Classifies_http_errors_as_transient_or_permanent(HttpStatusCode code, bool transient)
    {
        var provider = Build(new StubHandler((_, _, _) => Task.FromResult(Status(code, new { error = "call +201001234567 failed" }))),
            new() { ["WhatsApp:WebJs:ReadRetries"] = "0" });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "hi", Guid.NewGuid(), default));
        Assert.Equal(transient, ex.IsTransient);
        Assert.DoesNotContain("201001234567", ex.Message); // provider error text is redacted
    }

    [Fact]
    public async Task Idempotent_reads_are_retried_but_sends_never_are()
    {
        var reads = new StubHandler((_, n, _) => Task.FromResult(n < 3 ? Status(HttpStatusCode.ServiceUnavailable) : Status(HttpStatusCode.OK, new { status = "Connected" })));
        Assert.Equal(SessionStatus.Connected, await Build(reads, new() { ["WhatsApp:WebJs:BreakerMinimumThroughput"] = "50" }).GetStatusAsync(Guid.NewGuid(), default));
        Assert.Equal(3, reads.Calls);

        var sends = new StubHandler((_, _, _) => Task.FromResult(Status(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<ProviderException>(() => Build(sends).SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "hi", Guid.NewGuid(), default));
        Assert.Equal(1, sends.Calls); // a retried POST could deliver twice; the outbox owns send retries
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_fails_fast_without_calling_the_sidecar()
    {
        var stub = new StubHandler((_, _, _) => Task.FromResult(Status(HttpStatusCode.InternalServerError)));
        var provider = Build(stub, new() { ["WhatsApp:WebJs:ReadRetries"] = "0" });

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<ProviderException>(() => provider.SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "hi", Guid.NewGuid(), default));
        var callsBeforeOpen = stub.Calls;

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "hi", Guid.NewGuid(), default));
        Assert.True(ex.IsTransient);
        Assert.Contains("circuit", ex.Message);
        Assert.Equal(callsBeforeOpen, stub.Calls);
    }

    [Fact]
    public async Task A_slow_sidecar_times_out_as_a_transient_failure()
    {
        var stub = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Status(HttpStatusCode.OK);
        });
        var provider = Build(stub);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "hi", Guid.NewGuid(), default));
        Assert.True(ex.IsTransient);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_provider_failure()
    {
        var stub = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Status(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(stub).SendTextToPhoneAsync(Guid.NewGuid(), "+201001234567", "hi", Guid.NewGuid(), cts.Token));
    }
}
