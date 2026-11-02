using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using Rasel.Application;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;
using Rasel.Domain.Entities;
using Rasel.Infrastructure;
using Rasel.Infrastructure.Persistence;
using Rasel.Infrastructure.Providers;

namespace Rasel.Infrastructure.Tests;

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

public sealed class TestEnvironment(string name = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "Rasel.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>The real application + infrastructure wired against a throw-away SQLite file (no web host, no background worker).</summary>
public sealed class Rig : IAsyncDisposable
{
    public const string ApiKey = "rig-api-key-0123456789";

    private readonly string _dbPath = Path.Combine(@"C:\DevSpace\claude-scratch\rasel-infra-tests", $"{Guid.NewGuid():N}.db");

    public ServiceProvider Services { get; private set; } = null!;
    public FakeClock Clock { get; } = new();
    public ApiClient Client { get; private set; } = null!;
    public FakeWhatsAppProvider WhatsApp => Services.GetRequiredService<FakeWhatsAppProvider>();

    public static async Task<Rig> CreateAsync(Dictionary<string, string?>? settings = null, string environment = "Development")
    {
        var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig._dbPath)!);
        var values = new Dictionary<string, string?>
        {
            ["Database:Path"] = rig._dbPath,
            ["Security:OtpPepper"] = "rig-otp-pepper-0123456789-0123456789",
            ["Security:TokenSecret"] = "rig-token-secret-0123456789-0123456789",
            ["Seed:DevApiKey"] = ApiKey,
            ["Outbox:MinDelayMs"] = "0",
            ["Outbox:MaxDelayMs"] = "0",
        };
        foreach (var (k, v) in settings ?? []) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddSingleton<TimeProvider>(rig.Clock);
        services.AddRaselApplication(config);
        services.AddRaselInfrastructure(config);
        rig.Services = services.BuildServiceProvider();

        await rig.Services.InitializeRaselAsync();
        await using var scope = rig.Services.CreateAsyncScope();
        rig.Client = (await scope.ServiceProvider.GetRequiredService<IApiClientRepository>()
            .FindByKeyHashAsync(Rasel.Application.Common.ApiKeyHasher.Hash(ApiKey), default))!;
        return rig;
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task<Guid> ConnectedSessionAsync()
    {
        var id = await InScopeAsync(async sp => (await sp.GetRequiredService<CreateSessionHandler>().HandleAsync(Client.Id, "main", default)).Id);
        await InScopeAsync(async sp => await sp.GetRequiredService<GetSessionQrHandler>().HandleAsync(Client.Id, id, default));
        return id;
    }

    public async Task<IReadOnlyList<Message>> AllMessagesAsync() =>
        await InScopeAsync(async sp => (IReadOnlyList<Message>)[.. sp.GetRequiredService<RaselDbContext>().Messages.OrderBy(m => m.CreatedAt)]);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(file); } catch (IOException) { }
    }
}
