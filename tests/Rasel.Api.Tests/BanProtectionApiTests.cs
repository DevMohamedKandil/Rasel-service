using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rasel.Api.Tests;

public class BanProtectionApiTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient c, string url, object body)
    {
        var response = await c.PostAsJsonAsync(url, body);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Recipient_daily_limit_returns_429_problem_json()
    {
        await using var f = new RaselFactory(settings: new() { ["Pacing:DailyPerRecipient"] = "2" });
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(c, "/v1/messages", new { phone = "+201001234567", text = "hi" })).Status);

        var response = await c.PostAsJsonAsync("/v1/messages", new { phone = "+201001234567", text = "hi" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("recipient_daily_limit", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_message_is_client_scoped_and_hides_content()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        var (_, sent) = await PostAsync(c, "/v1/messages", new { phone = "+201001234567", text = "top secret" });
        var id = sent.GetProperty("messageId").GetGuid();

        JsonElement body = default;
        for (var i = 0; i < 100; i++)
        {
            body = await c.GetFromJsonAsync<JsonElement>($"/v1/messages/{id}");
            if (body.GetProperty("status").GetString() == "Sent") break;
            await Task.Delay(50);
        }
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("attempts").GetInt32());
        var raw = body.GetRawText();
        Assert.DoesNotContain("top secret", raw);
        Assert.DoesNotContain("1234567", raw);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/v1/messages/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync($"/v1/messages/{id}")).StatusCode);
    }
}
