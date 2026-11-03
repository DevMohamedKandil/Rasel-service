using System.Net;
using Rasel.Api.Security;

namespace Rasel.Api.Tests;

public class CorsTests
{
    private static HttpRequestMessage Preflight(string origin) => new(HttpMethod.Options, "/v1/sessions")
    {
        Headers = { { "Origin", origin }, { "Access-Control-Request-Method", "POST" }, { "Access-Control-Request-Headers", "x-api-key,content-type" } },
    };

    [Fact]
    public async Task Disabled_by_default_no_cors_headers()
    {
        await using var f = new RaselFactory();
        var r = await f.CreateClient().SendAsync(Preflight("https://admin.example.com"));
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Listed_origin_gets_preflight_without_api_key_and_others_do_not()
    {
        await using var f = new RaselFactory(settings: new() { ["Cors:AllowedOrigins:0"] = "https://admin.example.com" });
        var client = f.CreateClient();
        var ok = await client.SendAsync(Preflight("https://admin.example.com"));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("https://admin.example.com", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("x-api-key", string.Join(",", ok.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);

        var other = await client.SendAsync(Preflight("https://evil.example.com"));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://a.example.com/")]
    [InlineData("https://a.example.com/path")]
    [InlineData("not a url")]
    public void Validator_rejects_wildcards_and_non_origins(string origin) =>
        Assert.True(new CorsOptionsValidator().Validate(null, new CorsOptions { AllowedOrigins = [origin] }).Failed);

    [Fact]
    public void Validator_accepts_explicit_origins() =>
        Assert.True(new CorsOptionsValidator().Validate(null, new CorsOptions { AllowedOrigins = ["https://a.example.com", "http://localhost:4200"] }).Succeeded);
}
