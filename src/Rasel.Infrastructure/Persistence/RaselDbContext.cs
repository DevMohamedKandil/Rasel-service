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
        builder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ApiClient>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.ApiKeyHash).HasMaxLength(64);
            e.HasIndex(c => c.ApiKeyHash).IsUnique();
            e.Property(c => c.Scope).HasConversion<int>();
        });

        model.Entity<Session>().Property(s => s.Name).HasMaxLength(100);

        model.Entity<Message>(e =>
        {
            e.Property(m => m.Target).HasMaxLength(128);
            e.Property(m => m.Destination).HasMaxLength(128);
            e.Property(m => m.Body).HasMaxLength(4096);
            e.Property(m => m.IdempotencyKey).HasMaxLength(128);
            e.Property(m => m.Fingerprint).HasMaxLength(64);
            e.Property(m => m.RecipientKey).HasMaxLength(64);
            e.HasIndex(m => new { m.ClientId, m.RecipientKey, m.CreatedAt });
            e.Property(m => m.Error).HasMaxLength(256);
            e.Property(m => m.Version).IsConcurrencyToken();
            e.HasIndex(m => new { m.ClientId, m.CreatedAt });
            // The outbox worker scans pending rows by due time.
            e.HasIndex(m => new { m.Status, m.NextAttemptAt });
            // Idempotency: one message per (client, key); rows without a key are unconstrained.
            e.HasIndex(m => new { m.ClientId, m.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });

        model.Entity<OtpChallenge>(e =>
        {
            e.Property(c => c.PhoneE164).HasMaxLength(20);
            e.Property(c => c.Purpose).HasMaxLength(64);
            e.Property(c => c.CodeHash).HasMaxLength(64);
            e.Property(c => c.Version).IsConcurrencyToken();
            e.HasIndex(c => new { c.ClientId, c.PhoneE164, c.Purpose });
            // At most one Active (Status = 0) challenge per (client, phone, purpose): closes the parallel-request race.
            e.HasIndex(c => new { c.ClientId, c.PhoneE164, c.Purpose }, "UX_OtpChallenges_Active")
                .IsUnique().HasFilter("\"Status\" = 0");
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    private sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        v => v == null ? null : v.Value.UtcTicks, v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));
}
