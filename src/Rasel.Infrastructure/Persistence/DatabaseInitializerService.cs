using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Rasel.Infrastructure.Persistence;

/// <summary>
/// Migrates and seeds the database as the first hosted service. The host validates options before starting any hosted
/// service, so a bad configuration fails before the database is touched; and it is registered before the outbox worker,
/// which therefore never sees a missing schema.
/// </summary>
public sealed class DatabaseInitializerService(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => services.InitializeRaselAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
