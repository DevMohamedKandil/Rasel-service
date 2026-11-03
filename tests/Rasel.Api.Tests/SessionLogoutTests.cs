using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rasel.Api.Tests;

public class SessionLogoutTests
{
    [Fact]
    public async Task Logout_unlinks_the_number_and_the_session_needs_a_new_qr()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        var id = await f.CreateConnectedSessionAsync(c);

        var r = await c.PostAsync($"/v1/sessions/{id}/logout", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("201000000000", body);
        Assert.Equal("NeedsQr", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());

        // Not connected any more: no linked number, and a second logout is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await c.GetAsync($"/v1/sessions/{id}/linked-number")).StatusCode);
        var again = await c.PostAsync($"/v1/sessions/{id}/logout", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("session_not_connected", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logout_of_an_unknown_session_is_404()
    {
        await using var f = new RaselFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateAuthorizedClient().PostAsync($"/v1/sessions/{Guid.NewGuid()}/logout", null)).StatusCode);
    }
}
