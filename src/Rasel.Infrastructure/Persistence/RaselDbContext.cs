using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Rasel.Domain.Entities;

namespace Rasel.Infrastructure.Persistence;

public sealed class RaselDbContext(DbContextOptions<RaselDbContext> options) : DbContext(options)
{
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite cannot compare DateTimeOffset values; UTC ticks keep ordering and range queries working.
        builder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ApiClient>().HasIndex(c => c.ApiKeyHash).IsUnique();
        model.Entity<Message>().HasIndex(m => new { m.ClientId, m.CreatedAt });
        model.Entity<OtpChallenge>().HasIndex(c => new { c.ClientId, c.PhoneE164, c.Purpose });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
}
