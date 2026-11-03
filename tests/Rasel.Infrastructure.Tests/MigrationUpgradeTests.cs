using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Persistence;

namespace Rasel.Infrastructure.Tests;

public class MigrationUpgradeTests
{
    [Fact]
    public async Task Keys_created_before_scopes_become_Admin_when_the_schema_is_upgraded()
    {
        var path = Path.Combine(Path.GetTempPath(), "rasel-tests", $"mig-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var db = new RaselDbContext(new DbContextOptionsBuilder<RaselDbContext>().UseSqlite($"Data Source={path}").Options))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20261103011830_RecipientKey"); // the previous schema: no Scope column
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO ApiClients (Id, Name, ApiKeyHash, DailyMessageLimit, Active) VALUES ('11111111-1111-1111-1111-111111111111', 'legacy', 'ABC', 5, 1)");
                Assert.DoesNotContain("ApiKeyScopes", (await db.Database.GetAppliedMigrationsAsync()).Last());
            }
            await using (var db = new RaselDbContext(new DbContextOptionsBuilder<RaselDbContext>().UseSqlite($"Data Source={path}").Options))
            {
                await db.Database.MigrateAsync();
                var legacy = await db.ApiClients.SingleAsync();
                Assert.Equal(ApiKeyScope.Admin, legacy.Scope);
                Assert.True(legacy.Active);
                Assert.Null(legacy.LastUsedAt);
                Assert.Null(legacy.ExpiresAt);
            }
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch (IOException) { } }
    }
}
