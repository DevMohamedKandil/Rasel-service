using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rasel.Api.Tests;

public class LinkedNumberApiTests
{
    [Fact]
    public async Task Normal_payloads_are_masked_and_the_full_number_comes_only_from_the_no_store_endpoint()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        var id = await f.CreateConnectedSessionAsync(c);

        var status = await (await c.GetAsync($"/v1/sessions/{id}")).Content.ReadAsStringAsync();
        var qr = await (await c.GetAsync($"/v1/sessions/{id}/qr")).Content.ReadAsStringAsync();
        foreach (var body in new[] { status, qr })
        {
            Assert.DoesNotContain("201000000000", body);
            Assert.Equal("+20100****000", JsonDocument.Parse(body).RootElement.GetProperty("linkedPhoneMasked").GetString());
        }

        var full = await c.GetAsync($"/v1/sessions/{id}/linked-number");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Contains("no-store", full.Headers.CacheControl!.ToString());
        Assert.Equal("+201000000000", (await full.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Linked_number_needs_a_key_and_hides_unknown_sessions()
    {
        await using var f = new RaselFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync($"/v1/sessions/{Guid.NewGuid()}/linked-number")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateAuthorizedClient().GetAsync($"/v1/sessions/{Guid.NewGuid()}/linked-number")).StatusCode);
    }
}
