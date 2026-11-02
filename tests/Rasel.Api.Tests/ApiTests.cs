using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rasel.Api.Tests;

public class ApiTests
{
    private const string Phone = "+201001234567";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient c, string url, object body)
    {
        var response = await c.PostAsJsonAsync(url, body);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static async Task<Guid> RequestOtpAsync(HttpClient c, string purpose = "login")
    {
        var (status, body) = await PostAsync(c, "/v1/otp/request", new { phone = Phone, purpose, channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("challengeId").GetGuid();
    }

    [Fact]
    public async Task Health_is_public_and_v1_requires_a_valid_api_key()
    {
        await using var f = new RaselFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/health")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync("/v1/sessions", new { name = "x" })).StatusCode);

        var bad = f.CreateClient();
        bad.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        var response = await bad.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Otp_round_trip_never_exposes_the_code_and_returns_a_token()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);

        var (status, body) = await PostAsync(c, "/v1/otp/request", new { phone = Phone, purpose = "login", channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["challengeId", "expiresInSeconds", "resendAfterSeconds"], body.EnumerateObject().Select(p => p.Name).Order());
        var code = f.LastOtpCode();
        Assert.DoesNotContain(code, body.GetRawText());

        var (verifyStatus, verified) = await PostAsync(c, "/v1/otp/verify", new { challengeId = body.GetProperty("challengeId"), code });
        Assert.Equal(HttpStatusCode.OK, verifyStatus);
        Assert.Equal(3, verified.GetProperty("token").GetString()!.Split('.').Length);
    }

    [Fact]
    public async Task Wrong_codes_count_attempts_then_lock_the_challenge()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        var id = await RequestOtpAsync(c);
        var wrong = f.LastOtpCode() == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, "/v1/otp/verify", new { challengeId = id, code = wrong })).Status);

        var (status, _) = await PostAsync(c, "/v1/otp/verify", new { challengeId = id, code = f.LastOtpCode() });
        Assert.Equal((HttpStatusCode)423, status);
    }

    [Fact]
    public async Task Expired_code_is_rejected_with_410()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        var id = await RequestOtpAsync(c);

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var (status, _) = await PostAsync(c, "/v1/otp/verify", new { challengeId = id, code = f.LastOtpCode() });
        Assert.Equal(HttpStatusCode.Gone, status);
    }

    [Fact]
    public async Task Resend_before_cooldown_is_429_with_retry_after()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        await RequestOtpAsync(c);

        var response = await c.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await RequestOtpAsync(c);
    }

    [Fact]
    public async Task Second_verify_of_the_same_challenge_is_rejected()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        var id = await RequestOtpAsync(c);
        var code = f.LastOtpCode();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, "/v1/otp/verify", new { challengeId = id, code })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, "/v1/otp/verify", new { challengeId = id, code })).Status);
    }

    [Fact]
    public async Task Group_message_is_rejected_when_zero_or_two_groups_match_and_sent_when_one_matches()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        var sessionId = await f.CreateConnectedSessionAsync(c);
        f.WhatsApp.AddGroup("Twin");
        f.WhatsApp.AddGroup("Twin");

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(c, "/v1/messages", new { sessionId, groupName = "Missing", text = "hi" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, "/v1/messages", new { sessionId, groupName = "Twin", text = "hi" })).Status);

        var (status, _) = await PostAsync(c, "/v1/messages", new { sessionId, groupName = "Rasel Demo Group", text = "hello team" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        await WaitForAsync(() => f.WhatsApp.SentMessages.Any(m => m.IsGroup && m.Text == "hello team"));
    }

    [Fact]
    public async Task Daily_message_limit_returns_429()
    {
        await using var f = new RaselFactory(dailyLimit: 2);
        var c = f.CreateAuthorizedClient();
        var sessionId = await f.CreateConnectedSessionAsync(c);

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(c, "/v1/messages", new { sessionId, phone = Phone, text = "hi" })).Status);
        var (status, body) = await PostAsync(c, "/v1/messages", new { sessionId, phone = Phone, text = "hi" });

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("daily_limit_reached", body.GetProperty("code").GetString());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), "Timed out waiting for the background worker.");
    }
}
