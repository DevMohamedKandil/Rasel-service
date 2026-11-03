using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Rasel.Application.Ports;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Configuration;
using Rasel.Infrastructure.Providers.Bridge;

namespace Rasel.Infrastructure.Tests;

/// <summary>HTTP stubs mirroring the real WPService bridge: plain-text bodies, 503/400/404/409 as in server/index.js.</summary>
public class BridgeProviderTests
{
    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Text(HttpStatusCode code, string text) => new(code) { Content = new StringContent(text, Encoding.UTF8, "text/html") };

    private static IWhatsAppProvider Build(Stub stub, string token = "")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WhatsApp:Provider"] = "Bridge",
            ["WhatsApp:Bridge:BaseUrl"] = "http://127.0.0.1:5001",
            ["WhatsApp:Bridge:ReadRetries"] = "0",
            ["WhatsApp:Bridge:SharedToken"] = token,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<WhatsAppOptions>().Bind(config.GetSection("WhatsApp"));
        services.AddBridgeProvider();
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = stub));
        return services.BuildServiceProvider().GetRequiredService<IWhatsAppProvider>();
    }

    [Fact]
    public async Task Shared_token_is_sent_as_header_only_when_configured()
    {
        var seen = new List<string?>();
        var stub = new Stub((r, _) => { seen.Add(r.Headers.TryGetValues("X-Bridge-Token", out var v) ? v.Single() : null); return Text(HttpStatusCode.OK, "SendDone"); });
        await Build(stub, "0123456789abcdef").SendTextToGroupAsync(Guid.NewGuid(), "G", "hi", Guid.NewGuid(), default);
        await Build(stub).SendTextToGroupAsync(Guid.NewGuid(), "G", "hi", Guid.NewGuid(), default);
        Assert.Equal(["0123456789abcdef", null], seen);
    }

    [Fact]
    public void Plain_http_to_a_non_loopback_host_needs_a_token()
    {
        var v = new WhatsAppOptionsValidator();
        WhatsAppOptions Make(string token) => new() { Provider = "Bridge", Bridge = { BaseUrl = "http://sidecar:5001", SharedToken = token } };
        Assert.True(v.Validate(null, Make("")).Failed);
        Assert.True(v.Validate(null, Make("short")).Failed);
        Assert.True(v.Validate(null, Make("0123456789abcdef")).Succeeded);
    }

    [Fact]
    public async Task Group_send_posts_the_real_bridge_contract()
    {
        var stub = new Stub((_, _) => Text(HttpStatusCode.OK, "SendDone"));
        await Build(stub).SendTextToGroupAsync(Guid.NewGuid(), "Afzaz Dev", "hello", Guid.NewGuid(), default);

        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/doSendPersonalizedTextToGroup", call.Url);
        Assert.Equal("""{"groupName":"Afzaz Dev","message":"hello"}""", call.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Maps_bridge_errors_to_transient_or_permanent(HttpStatusCode code, bool transient)
    {
        var provider = Build(new Stub((_, _) => Text(code, "bridge says no")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SendTextToGroupAsync(Guid.NewGuid(), "g", "t", Guid.NewGuid(), default));
        Assert.Equal(transient, ex.IsTransient);
    }

    [Fact]
    public async Task Status_probe_distinguishes_ready_from_not_ready_without_sending()
    {
        var ready = false;
        var qr = "";
        var stub = new Stub((req, _) => req.Method == HttpMethod.Get
            ? Text(HttpStatusCode.OK, qr)
            : ready ? Text(HttpStatusCode.BadRequest, "Group name is required") : Text(HttpStatusCode.ServiceUnavailable, "WhatsApp client not ready yet"));
        var provider = Build(stub);
        var id = Guid.NewGuid();

        Assert.Equal(SessionStatus.Starting, await provider.GetStatusAsync(id, default));
        qr = "<img src=\"data:...\">";
        Assert.Equal(SessionStatus.NeedsQr, await provider.GetStatusAsync(id, default));
        Assert.Equal(qr, await provider.GetQrAsync(id, default));
        ready = true;
        Assert.Equal(SessionStatus.Connected, await provider.GetStatusAsync(id, default));
        Assert.Null(await provider.GetQrAsync(id, default)); // stale QR hidden once connected

        // the probe always uses an empty group name, so the bridge can never send anything for it
        Assert.All(stub.Calls.Where(c => c.Method == HttpMethod.Post), c => Assert.Equal("""{"groupName":"","message":""}""", c.Body));
    }

    [Fact]
    public async Task Unreachable_bridge_is_transient_and_group_lookup_is_optimistic()
    {
        var provider = Build(new Stub((_, _) => throw new HttpRequestException("refused")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.SendTextToGroupAsync(Guid.NewGuid(), "g", "t", Guid.NewGuid(), default));
        Assert.True(ex.IsTransient);
        Assert.Equal("g", Assert.Single(await provider.FindGroupsByNameAsync(Guid.NewGuid(), "g", default)).Name);
    }

    [Fact]
    public async Task Phone_send_uses_the_get_endpoint_with_digits_only()
    {
        var stub = new Stub((_, _) => Text(HttpStatusCode.OK, "SendDone"));
        await Build(stub).SendTextToPhoneAsync(Guid.NewGuid(), "+966500000000", "a b", Guid.NewGuid(), default);
        Assert.Equal("/sendMessage?phone=966500000000&message=a%20b", Assert.Single(stub.Calls).Url);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("http://example.com:5001", false)]
    [InlineData("http://127.0.0.1:5001", true)]
    [InlineData("https://bridge.example.com", true)]
    public void Options_validation_for_bridge(string url, bool ok)
    {
        var o = new WhatsAppOptions { Provider = "Bridge", Bridge = { BaseUrl = url } };
        Assert.Equal(ok, new WhatsAppOptionsValidator().Validate(null, o).Succeeded);
        o.Bridge.BaseUrl = "http://127.0.0.1:5001";
        o.Bridge.AttemptTimeoutSeconds = 0;
        Assert.False(new WhatsAppOptionsValidator().Validate(null, o).Succeeded);
    }

    [Theory]
    [InlineData("{\"phone\":\"201000000880\"}", "201000000880")]
    [InlineData("{\"phone\":null}", null)]
    [InlineData("{\"phone\":\"abc\"}", null)]
    [InlineData("not json", null)]
    public async Task Linked_phone_is_read_from_info_and_validated(string json, string? expected)
    {
        var stub = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        Assert.Equal(expected, await Build(stub).GetLinkedPhoneAsync(Guid.NewGuid(), default));
        Assert.Equal("/info", stub.Calls.Single().Url);
    }

    [Fact]
    public async Task Linked_phone_is_null_on_the_legacy_bridge_without_info()
    {
        var stub = new Stub((_, _) => Text(HttpStatusCode.NotFound, "Cannot GET /info"));
        Assert.Null(await Build(stub).GetLinkedPhoneAsync(Guid.NewGuid(), default));
    }
}
