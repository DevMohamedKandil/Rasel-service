using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Providers;

namespace Rasel.Api.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
    public void Advance(TimeSpan by) => UtcNow += by;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>An isolated API instance: own SQLite file, controllable clock, no queue delay.</summary>
public sealed class RaselFactory(int dailyLimit = 1000, Dictionary<string, string>? settings = null, string environment = "Development") : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key-0123456789";
    private readonly string _dbPath = Path.Combine(@"C:\DevSpace\claude-scratch\rasel-tests", $"{Guid.NewGuid():N}.db");

    public TestClock Clock { get; } = new();
    public FakeWhatsAppProvider WhatsApp => Services.GetRequiredService<FakeWhatsAppProvider>();
    public FakeSmsProvider Sms => Services.GetRequiredService<FakeSmsProvider>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        builder.UseSetting("Database:Path", _dbPath);
        builder.UseSetting("Seed:DevApiKey", ApiKey);
        builder.UseSetting("Seed:DevDailyMessageLimit", dailyLimit.ToString());
        builder.UseSetting("Outbox:MinDelayMs", "0");
        builder.UseSetting("Outbox:MaxDelayMs", "0");
        builder.UseSetting("Outbox:PollIntervalMs", "20");
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings ?? []) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public async Task<Guid> CreateConnectedSessionAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/v1/sessions", new { name = "main" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        (await client.GetAsync($"/v1/sessions/{id}/qr")).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>The OTP code exists only inside the provider message, never in an API response.</summary>
    public string LastOtpCode() => Regex.Match(WhatsApp.SentMessages.Last().Text, @"\b\d{6}\b").Value;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    public sealed record IdDto(Guid Id);
}
