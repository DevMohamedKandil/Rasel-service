using System.Net;

namespace Rasel.Api.Tests;

public class ReviewFixApiTests
{
    private static async Task<HttpStatusCode> BadKeyFrom(HttpClient client, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/sessions/{Guid.NewGuid()}");
        request.Headers.Add("X-Api-Key", "wrong-key-wrong-key-0000");
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_by_default_so_a_spoofed_header_cannot_dodge_the_throttle()
    {
        await using var f = new RaselFactory(settings: new() { ["Api:FailedAuthPerMinute"] = "2" });
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await BadKeyFrom(client, "10.0.0.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await BadKeyFrom(client, "10.0.0.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await BadKeyFrom(client, "10.0.0.3"));
    }

    [Fact]
    public async Task With_TrustForwardedHeaders_the_throttle_is_per_forwarded_client_not_per_proxy()
    {
        await using var f = new RaselFactory(settings: new() { ["Api:FailedAuthPerMinute"] = "2", ["Api:TrustForwardedHeaders"] = "true" });
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await BadKeyFrom(client, "10.0.0.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await BadKeyFrom(client, "10.0.0.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await BadKeyFrom(client, "10.0.0.1"));
        // another real client behind the same proxy is unaffected
        Assert.Equal(HttpStatusCode.Unauthorized, await BadKeyFrom(client, "10.0.0.9"));
        // and a valid key from a throttled address is still throttled, a valid key from a clean address works
        var ok = f.CreateAuthorizedClient();
        ok.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.9");
        Assert.Equal(HttpStatusCode.NotFound, (await ok.GetAsync($"/v1/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Short_seed_api_key_is_refused_outside_development()
    {
        await using var f = new RaselFactory(environment: "Production", settings: new()
        {
            ["Security:OtpPepper"] = "prod-pepper-0123456789-0123456789-ab",
            ["Security:TokenSecret"] = "prod-token-0123456789-0123456789-cd",
            ["Seed:DevApiKey"] = "short",
        });
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }
}
