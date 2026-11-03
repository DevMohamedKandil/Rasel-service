using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rasel.Api.Tests;

public class SystemEndpointsTests
{
    [Fact]
    public async Task Status_requires_a_key_and_reports_Disabled_when_the_sidecar_is_not_managed()
    {
        await using var f = new RaselFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/v1/system/status")).StatusCode);
        var response = await f.CreateAuthorizedClient().GetAsync("/v1/system/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Fake", body.GetProperty("provider").GetString());
        Assert.Equal("Disabled", body.GetProperty("sidecar").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task Sidecar_commands_answer_409_when_not_managed_and_401_without_a_key(string action)
    {
        await using var f = new RaselFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsync($"/v1/system/sidecar/{action}", null)).StatusCode);
        var response = await f.CreateAuthorizedClient().PostAsync($"/v1/system/sidecar/{action}", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sidecar_not_managed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
