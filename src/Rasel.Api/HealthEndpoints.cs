using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rasel.Infrastructure;

namespace Rasel.Api;

public static class HealthEndpoints
{
    /// <summary>
    /// /health/live: process is up (no dependencies, for liveness probes; /health is an alias).
    /// /health/ready: database reachable (503 when not) and outbox draining (Degraded stays 200).
    /// Responses never include exception details.
    /// </summary>
    public static void MapRaselHealth(this IEndpointRouteBuilder app)
    {
        var live = new HealthCheckOptions { Predicate = _ => false, ResponseWriter = Write };
        var ready = new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(DependencyInjection.ReadyTag),
            ResponseWriter = Write,
        };
        app.MapHealthChecks("/health", live).AllowAnonymous().ExcludeFromDescription();
        app.MapHealthChecks("/health/live", live).AllowAnonymous().ExcludeFromDescription();
        app.MapHealthChecks("/health/ready", ready).AllowAnonymous().ExcludeFromDescription();
    }

    private static Task Write(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status == HealthStatus.Healthy ? "ok" : report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString().ToLowerInvariant(), detail = e.Value.Description }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
