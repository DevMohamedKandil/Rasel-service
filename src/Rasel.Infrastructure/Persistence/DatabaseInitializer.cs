using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rasel.Infrastructure.Configuration;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>Creates the schema and seeds the dev client when the database has none. Runs once at startup.</summary>
    public static async Task InitializeRaselAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Rasel.Database");
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        var db = scope.ServiceProvider.GetRequiredService<RaselDbContext>();
        // WAL lets readers and the single writer proceed concurrently (API requests vs. the outbox worker). Persisted in the file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        if (scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
        {
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Database migrations applied");
        }
        else if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new InvalidOperationException("The database has pending migrations and Database:MigrateOnStartup is false. Run the migrations first.");

        var clients = scope.ServiceProvider.GetRequiredService<IApiClientRepository>();
        if (await clients.AnyAsync(ct)) return;

        var configured = config["Seed:DevApiKey"];
        var key = string.IsNullOrWhiteSpace(configured) ? "rsl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() : configured;
        if (!string.IsNullOrWhiteSpace(configured) && configured.Length < 16 && !env.IsDevelopment())
            throw new InvalidOperationException("Seed:DevApiKey must be at least 16 characters outside Development.");
        var limit = config.GetValue("Seed:DevDailyMessageLimit", 1000);
        await clients.AddAsync(ApiClient.Create("dev", ApiKeyHasher.Hash(key), limit, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow(), ApiKeyScope.Admin), ct);

        if (!string.IsNullOrWhiteSpace(configured))
            logger.LogInformation("Seeded dev client with the API key from configuration (Seed:DevApiKey).");
        else if (env.IsDevelopment())
            logger.LogWarning("Seeded dev client. API key (shown ONCE, store it now): {ApiKey}", key);
        else
            logger.LogWarning("Seeded dev client with a random API key that is NOT logged outside Development; set Seed__DevApiKey to choose one.");
    }
}
