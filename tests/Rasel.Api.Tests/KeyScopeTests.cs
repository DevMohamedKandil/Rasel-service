using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Rasel.Api.Tests;

public class KeyScopeTests
{
    private static readonly Guid AnyId = Guid.NewGuid();

    // The complete /v1 surface. A new endpoint must be added here (Every_v1_endpoint_declares_an_explicit_policy_and_is_in_the_matrix) and declare a policy.
    public static TheoryData<string, string, bool> Matrix => new()
    {
        { "GET", "/v1/system/status", false },
        { "POST", "/v1/system/sidecar/start", true },
        { "POST", "/v1/system/sidecar/stop", true },
        { "POST", "/v1/system/sidecar/restart", true },
        { "POST", "/v1/sessions/", false },
        { "GET", "/v1/sessions/{id}", false },
        { "GET", "/v1/sessions/{id}/qr", false },
        { "GET", "/v1/sessions/{id}/linked-number", false },
        { "DELETE", "/v1/sessions/{id}", true },
        { "POST", "/v1/messages", false },
        { "GET", "/v1/messages/{id}", false },
        { "POST", "/v1/otp/request", false },
        { "POST", "/v1/otp/verify", false },
        { "GET", "/v1/keys/", true },
        { "POST", "/v1/keys/", true },
        { "POST", "/v1/keys/{id}/revoke", true },
        { "POST", "/v1/keys/{id}/rotate", true },
    };

    private static async Task<(string Id, string Key)> NewKeyAsync(HttpClient admin, string scope, string name = "k")
    {
        var r = await admin.PostAsJsonAsync("/v1/keys", new { name, scope });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (j.GetProperty("key").GetProperty("id").GetString()!, j.GetProperty("apiKey").GetString()!);
    }

    private static HttpClient As(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> f, string key)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", key);
        return c;
    }

    private static HttpRequestMessage Req(string method, string path) => new(new HttpMethod(method), path.Replace("{id}", AnyId.ToString()))
    {
        Content = method is "POST" ? JsonContent.Create(new { }) : null,
    };

    [Theory, MemberData(nameof(Matrix))]
    public async Task Scope_matrix(string method, string path, bool adminOnly)
    {
        await using var f = new RaselFactory();
        var (_, std) = await NewKeyAsync(f.CreateAuthorizedClient(), "Standard");

        var anon = await f.CreateClient().SendAsync(Req(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var s = await As(f, std).SendAsync(Req(method, path));
        if (adminOnly)
        {
            Assert.Equal(HttpStatusCode.Forbidden, s.StatusCode);
            Assert.Equal("application/problem+json", s.Content.Headers.ContentType!.MediaType);
            Assert.Equal("forbidden_scope", (await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        else Assert.NotEqual(HttpStatusCode.Forbidden, s.StatusCode);

        var a = await f.CreateAuthorizedClient().SendAsync(Req(method, path));
        Assert.NotEqual(HttpStatusCode.Forbidden, a.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, a.StatusCode);
    }

    private static string Norm(string p) => Regex.Replace(p.TrimEnd('/'), @"\{id(:guid)?\}", "{id}");

    [Fact]
    public async Task Every_v1_endpoint_declares_an_explicit_policy_and_is_in_the_matrix()
    {
        await using var f = new RaselFactory();
        var endpoints = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/v1")).ToList();
        Assert.NotEmpty(endpoints);

        var matrix = Matrix.Select(r => (string)r[0] + " " + Norm((string)r[1])).ToHashSet();
        foreach (var e in endpoints)
        {
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.True(policies.Contains("RequireStandard") || policies.Contains("RequireAdmin"), $"{e.RoutePattern.RawText} has no explicit scope policy");
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
            foreach (var m in e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
                Assert.True(matrix.Contains(m + " " + Norm(e.RoutePattern.RawText!)), $"{m} {e.RoutePattern.RawText} missing from the scope matrix");
        }
        Assert.Equal(matrix.Count, endpoints.Sum(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Count));
    }

    [Fact]
    public async Task Standard_key_sends_messages_but_status_hides_engine_internals()
    {
        await using var f = new RaselFactory();
        var (_, std) = await NewKeyAsync(f.CreateAuthorizedClient(), "Standard");
        var c = As(f, std);
        var session = await f.CreateConnectedSessionAsync(c);
        var send = await c.PostAsJsonAsync("/v1/messages", new { sessionId = session, phone = "+201000000001", text = "hi" });
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var status = await c.GetFromJsonAsync<JsonElement>("/v1/system/status");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("sidecar").GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Create_requires_a_scope_and_a_valid_name_and_shows_the_key_once()
    {
        await using var f = new RaselFactory();
        var admin = f.CreateAuthorizedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/v1/keys", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/v1/keys", new { name = "x", scope = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/v1/keys", new { name = " ", scope = "Standard" })).StatusCode);

        var created = await admin.PostAsJsonAsync("/v1/keys", new { name = "billing", scope = "Standard" });
        Assert.Contains("no-store", created.Headers.CacheControl!.ToString());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var key = body.GetProperty("apiKey").GetString()!;
        Assert.True(key.Length >= 32 && key.StartsWith("rsl_"));

        var list = await admin.GetStringAsync("/v1/keys");
        Assert.DoesNotContain(key, list);
        Assert.DoesNotContain(Rasel.Application.Common.ApiKeyHasher.Hash(key), list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", list, StringComparison.OrdinalIgnoreCase);
        var rows = JsonDocument.Parse(list).RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var row = rows.Single(r => r.GetProperty("name").GetString() == "billing");
        Assert.Equal("Standard", row.GetProperty("scope").GetString());
        Assert.True(row.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastUsedAt").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await As(f, key).GetAsync("/v1/system/status")).StatusCode);
        var after = JsonDocument.Parse(await admin.GetStringAsync("/v1/keys")).RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "billing");
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task Revoked_key_gets_the_same_401_as_an_unknown_key()
    {
        await using var f = new RaselFactory();
        var admin = f.CreateAuthorizedClient();
        var (id, key) = await NewKeyAsync(admin, "Standard");
        Assert.Equal(HttpStatusCode.OK, (await As(f, key).GetAsync("/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/v1/keys/{id}/revoke", null)).StatusCode);

        var revoked = await As(f, key).GetAsync("/v1/system/status");
        var unknown = await As(f, "rsl_" + new string('0', 48)).GetAsync("/v1/system/status");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        static string Shape(string json) => System.Text.RegularExpressions.Regex.Replace(json, "\"traceId\":\"[^\"]*\"", "");
        Assert.Equal(Shape(await unknown.Content.ReadAsStringAsync()), Shape(await revoked.Content.ReadAsStringAsync()));
        Assert.Equal(unknown.Headers.WwwAuthenticate.ToString(), revoked.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/v1/keys/{id}/revoke", null)).StatusCode); // idempotent
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/v1/keys/{Guid.NewGuid()}/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task The_last_active_admin_key_cannot_be_revoked_but_can_once_another_exists()
    {
        await using var f = new RaselFactory();
        var admin = f.CreateAuthorizedClient();
        var seedId = JsonDocument.Parse(await admin.GetStringAsync("/v1/keys")).RootElement[0].GetProperty("id").GetString();
        var r = await admin.PostAsync($"/v1/keys/{seedId}/revoke", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("last_admin_key", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var (id2, key2) = await NewKeyAsync(admin, "Admin", "second");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/v1/keys/{seedId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await As(f, key2).PostAsync($"/v1/keys/{id2}/revoke", null)).StatusCode);
        await NewKeyAsync(As(f, key2), "Standard"); // a standard key does not count as an admin
        Assert.Equal(HttpStatusCode.Conflict, (await As(f, key2).PostAsync($"/v1/keys/{id2}/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task Rotation_without_grace_revokes_the_old_key_at_once()
    {
        await using var f = new RaselFactory();
        var admin = f.CreateAuthorizedClient();
        var (id, oldKey) = await NewKeyAsync(admin, "Standard");
        var r = await admin.PostAsync($"/v1/keys/{id}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var newKey = body.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal("Standard", body.GetProperty("key").GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(f, oldKey).GetAsync("/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(f, newKey).GetAsync("/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/v1/keys/{id}/rotate", null)).StatusCode);
    }

    [Fact]
    public async Task Rotation_grace_keeps_the_old_key_until_the_grace_ends()
    {
        await using var f = new RaselFactory();
        var admin = f.CreateAuthorizedClient();
        var (id, oldKey) = await NewKeyAsync(admin, "Standard");
        var r = await admin.PostAsJsonAsync($"/v1/keys/{id}/rotate", new { graceSeconds = 600 });
        var newKey = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await As(f, oldKey).GetAsync("/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(f, newKey).GetAsync("/v1/system/status")).StatusCode);
        f.Clock.Advance(TimeSpan.FromSeconds(601));
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(f, oldKey).GetAsync("/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(f, newKey).GetAsync("/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/v1/keys/{id}/rotate", new { graceSeconds = -1 })).StatusCode);
    }

    [Fact]
    public async Task Configured_grace_applies_when_the_request_gives_none()
    {
        await using var f = new RaselFactory(settings: new() { ["Api:RotationGraceSeconds"] = "300" });
        var admin = f.CreateAuthorizedClient();
        var (id, oldKey) = await NewKeyAsync(admin, "Standard");
        await admin.PostAsync($"/v1/keys/{id}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, (await As(f, oldKey).GetAsync("/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Rotating_the_only_admin_key_keeps_an_admin_available()
    {
        await using var f = new RaselFactory();
        var admin = f.CreateAuthorizedClient();
        var seedId = JsonDocument.Parse(await admin.GetStringAsync("/v1/keys")).RootElement[0].GetProperty("id").GetString();
        var r = await admin.PostAsync($"/v1/keys/{seedId}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var newKey = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await As(f, newKey).GetAsync("/v1/keys")).StatusCode);
    }

    private sealed class Capture : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState state, Exception? ex, Func<TState, Exception?, string> fmt) { lock (Lines) Lines.Add(fmt(state, ex)); }
        public void Dispose() { }
    }

    [Fact]
    public async Task Operations_are_audited_and_no_key_or_hash_reaches_the_logs()
    {
        var capture = new Capture();
        await using var f = new RaselFactory();
        await using var logged = f.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddLogging(l => l.AddProvider(capture))));
        var admin = logged.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", RaselFactory.ApiKey);
        var (id, key) = await NewKeyAsync(admin, "Standard");
        var rot = await (await admin.PostAsync($"/v1/keys/{id}/rotate", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newKey = rot.GetProperty("apiKey").GetString()!;
        await As(logged, key).GetAsync("/v1/system/status");
        await admin.PostAsync($"/v1/keys/{rot.GetProperty("key").GetProperty("id").GetString()}/revoke", null);

        string[] lines;
        lock (capture.Lines) lines = [.. capture.Lines];
        Assert.Contains(lines, l => l.Contains("AUDIT key.create"));
        Assert.Contains(lines, l => l.Contains("AUDIT key.rotate"));
        Assert.Contains(lines, l => l.Contains("AUDIT key.revoke"));
        foreach (var secret in new[] { key, newKey, RaselFactory.ApiKey })
        {
            Assert.DoesNotContain(lines, l => l.Contains(secret));
            Assert.DoesNotContain(lines, l => l.Contains(Rasel.Application.Common.ApiKeyHasher.Hash(secret), StringComparison.OrdinalIgnoreCase));
        }
    }
}
