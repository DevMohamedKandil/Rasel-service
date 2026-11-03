using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Sidecar;

/// <summary>Reports the sidecar state in /health/ready. Never Unhealthy: the API itself is fine while WhatsApp prepares, needs a QR or is stopped on purpose.</summary>
public sealed class SidecarHealthCheck(ISidecarControl control) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var s = control.Status;
        var text = $"{s.State}" + (s.Step is null ? "" : $": {s.Step}") + (s.Reason is null ? "" : $": {s.Reason}");
        return Task.FromResult(s.State is SidecarState.Connected or SidecarState.NeedsQr or SidecarState.Disabled
            ? HealthCheckResult.Healthy(text)
            : HealthCheckResult.Degraded(text));
    }
}
