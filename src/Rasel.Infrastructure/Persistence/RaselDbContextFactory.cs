using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rasel.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> at design time (migrations); the running app configures the context in DI.</summary>
public sealed class RaselDbContextFactory : IDesignTimeDbContextFactory<RaselDbContext>
{
    public RaselDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RaselDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
