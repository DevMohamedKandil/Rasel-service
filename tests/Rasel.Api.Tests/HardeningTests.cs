using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Rasel.Api.Tests;

public class HardeningTests
{
    private const string Phone = "+201001234567";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static readonly Dictionary<string, string> ProductionSecrets = new()
    {
        ["Security:OtpPepper"] = "prod-pepper-0123456789-0123456789-ab",
        ["Security:TokenSecret"] = "prod-token-0123456789-0123456789-cd",
    };

    // ---- authentication ----------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_or_wrong_key_is_401_problem_json_with_a_stable_code()
    {
        await using var f = new RaselFactory();
        foreach (var client in new[] { f.CreateClient(), WithKey(f, "wrong-key-wrong-key") })
        {
            var response = await client.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("unauthorized", (await ReadAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Authentication_is_fail_closed_for_every_endpoint_except_health()
    {
        await using var f = new RaselFactory();
        f.CreateClient(); // build the host
        var fallback = await f.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);

        var endpoints = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var path = endpoint.RoutePattern.RawText!;
            Assert.Equal(path.StartsWith("/health"), anonymous);
        }
    }

    [Fact]
    public async Task Repeated_wrong_keys_get_the_client_ip_throttled_before_any_lookup()
    {
        await using var f = new RaselFactory(settings: new() { ["Api:FailedAuthPerMinute"] = "3" });
        var bad = WithKey(f, "wrong-key-wrong-key");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync($"/v1/sessions/{Guid.NewGuid()}")).StatusCode);

        var throttled = await bad.GetAsync($"/v1/sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("auth_throttled", (await ReadAsync(throttled)).GetProperty("code").GetString());
        Assert.True(throttled.Headers.Contains("Retry-After"));

        // while throttled, even the right key from the same address is refused (guessing gets no oracle)
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.CreateAuthorizedClient().GetAsync($"/v1/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Per_key_request_quota_returns_429_with_retry_after_and_does_not_affect_health()
    {
        await using var f = new RaselFactory(settings: new() { ["Api:RequestsPerMinute"] = "3" });
        var c = f.CreateAuthorizedClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/v1/sessions/{Guid.NewGuid()}")).StatusCode);

        var limited = await c.GetAsync($"/v1/sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await ReadAsync(limited)).GetProperty("code").GetString());
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/health/live")).StatusCode);
    }

    // ---- error handling / ProblemDetails ----------------------------------------------------------------

    [Fact]
    public async Task Framework_errors_are_problem_json_too()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();

        var notFound = await c.GetAsync("/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("application/problem+json", notFound.Content.Headers.ContentType?.MediaType);

        var badJson = await c.PostAsync("/v1/otp/request", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Equal("application/problem+json", badJson.Content.Headers.ContentType?.MediaType);

        var wrongType = await c.PostAsync("/v1/otp/request", new StringContent("x", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
    }

    [Fact]
    public async Task Delivery_failing_on_every_channel_is_a_502_problem_not_a_500()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        var id = await f.CreateConnectedSessionAsync(c);
        f.WhatsApp.FailSends = true;
        var response = await c.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login", channel = "whatsapp" });
        // delivery falls back to SMS (works), so use a hard failure on both channels
        f.Sms.FailSends = true;
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        response = await c.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login", channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("otp_delivery_failed", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task Versioned_responses_carry_the_api_version_header()
    {
        await using var f = new RaselFactory();
        var response = await f.CreateAuthorizedClient().GetAsync($"/v1/sessions/{Guid.NewGuid()}");
        Assert.Equal("v1", response.Headers.GetValues("X-Api-Version").Single());
    }

    // ---- OTP over HTTP -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("1234567")]
    [InlineData("")]
    public async Task Malformed_codes_are_rejected_without_burning_an_attempt(string code)
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        var request = await c.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login" });
        var id = (await ReadAsync(request)).GetProperty("challengeId").GetGuid();

        var response = await c.PostAsJsonAsync("/v1/otp/verify", new { challengeId = id, code });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code_format", (await ReadAsync(response)).GetProperty("code").GetString());

        // the challenge still has all its attempts and the real code still works
        var verified = await c.PostAsJsonAsync("/v1/otp/verify", new { challengeId = id, code = f.LastOtpCode() });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
    }

    [Fact]
    public async Task Replayed_code_after_success_and_unknown_challenge_are_distinct_safe_errors()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        await f.CreateConnectedSessionAsync(c);
        var request = await c.PostAsJsonAsync("/v1/otp/request", new { phone = Phone, purpose = "login" });
        var id = (await ReadAsync(request)).GetProperty("challengeId").GetGuid();
        var code = f.LastOtpCode();

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/otp/verify", new { challengeId = id, code })).StatusCode);
        var replay = await c.PostAsJsonAsync("/v1/otp/verify", new { challengeId = id, code });
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("otp_already_used", (await ReadAsync(replay)).GetProperty("code").GetString());

        var unknown = await c.PostAsJsonAsync("/v1/otp/verify", new { challengeId = Guid.NewGuid(), code });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- idempotency + outbox over HTTP -----------------------------------------------------------------

    [Fact]
    public async Task Send_with_an_idempotency_key_is_delivered_once_and_replays_the_original_answer()
    {
        await using var f = new RaselFactory();
        var c = f.CreateAuthorizedClient();
        var sessionId = await f.CreateConnectedSessionAsync(c);

        async Task<HttpResponseMessage> SendAsync(string text, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = JsonContent.Create(new { sessionId, phone = Phone, text }) };
            request.Headers.Add("Idempotency-Key", key);
            return await c.SendAsync(request);
        }

        var first = await SendAsync("hello", "order-42");
        var second = await SendAsync("hello", "order-42");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal((await ReadAsync(first)).GetProperty("messageId").GetGuid(), (await ReadAsync(second)).GetProperty("messageId").GetGuid());
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        var reused = await SendAsync("something else", "order-42");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", (await ReadAsync(reused)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync("x", new string('k', 200))).StatusCode);

        await WaitForAsync(() => f.WhatsApp.SentMessages.Count == 1);
        await Task.Delay(300);
        Assert.Single(f.WhatsApp.SentMessages);
    }

    [Fact]
    public async Task Transient_provider_failure_is_retried_by_the_background_worker_until_it_succeeds()
    {
        await using var f = new RaselFactory(settings: new() { ["Outbox:BaseBackoffSeconds"] = "0", ["Outbox:MaxAttempts"] = "20" });
        var c = f.CreateAuthorizedClient();
        var sessionId = await f.CreateConnectedSessionAsync(c);
        f.WhatsApp.FailSends = true;
        f.WhatsApp.FailTransiently = true;

        var accepted = await c.PostAsJsonAsync("/v1/messages", new { sessionId, phone = Phone, text = "eventually" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await ReadAsync(accepted)).GetProperty("messageId").GetGuid();

        await WaitForAsync(async () => (await LoadAsync(f, id)).Attempts >= 2);
        Assert.Empty(f.WhatsApp.SentMessages);
        f.WhatsApp.FailSends = false;

        await WaitForAsync(() => f.WhatsApp.SentMessages.Any(m => m.Text == "eventually"));
        await WaitForAsync(async () => (await LoadAsync(f, id)).Status == MessageStatus.Sent);
        Assert.Null((await LoadAsync(f, id)).Body);
    }

    // ---- configuration / environments ---------------------------------------------------------------------

    [Fact]
    public async Task Production_refuses_to_start_with_the_development_placeholder_secrets()
    {
        await using var f = new RaselFactory(environment: "Production");
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("placeholder", Flatten(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_with_real_secrets_starts_and_does_not_expose_swagger()
    {
        await using var f = new RaselFactory(settings: ProductionSecrets, environment: "Production");
        var c = f.CreateAuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Development_serves_swagger()
    {
        await using var f = new RaselFactory();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    [Fact]
    public async Task Invalid_configuration_fails_at_startup_not_at_first_use()
    {
        await using var f = new RaselFactory(settings: new() { ["Outbox:MaxDelayMs"] = "-5" });
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Outbox", Flatten(ex));
    }

    // ---- health ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_endpoints_are_anonymous_and_ready_checks_the_database()
    {
        await using var f = new RaselFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);

        var ready = await c.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ReadAsync(ready);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("checks").EnumerateArray(), e => e.GetProperty("name").GetString() == "database");
    }

    [Fact]
    public async Task Validated_options_are_resolvable_from_the_running_host()
    {
        await using var f = new RaselFactory();
        f.CreateClient();
        Assert.Equal(5, f.Services.GetRequiredService<IOptions<Rasel.Application.Common.OtpOptions>>().Value.MaxAttempts);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static HttpClient WithKey(RaselFactory f, string key)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<Rasel.Domain.Entities.Message> LoadAsync(RaselFactory f, Guid id)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RaselDbContext>().Messages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private static string Flatten(Exception ex) => string.Join(" | ", Chain(ex).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException) yield return ex;
    }

    private static async Task WaitForAsync(Func<bool> condition) => await WaitForAsync(() => Task.FromResult(condition()));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++) await Task.Delay(50);
        Assert.True(await condition(), "Timed out waiting for the background worker.");
    }
}
