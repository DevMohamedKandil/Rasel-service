using Microsoft.Extensions.Options;

namespace Rasel.Infrastructure.Sidecar;

public sealed class SidecarOptions
{
    /// <summary>Writable folder for the sidecar copy, node_modules, session, token and logs. Empty = a "sidecar-data" folder next to the app data.</summary>
    public string DataDir { get; set; } = "";
    /// <summary>Folder bundled with the published API. Empty = "sidecar" next to the API binaries.</summary>
    public string SourceDir { get; set; } = "";
    public bool AutoStart { get; set; } = true;
    /// <summary>Obsolete: sessions get ports from the range below. Kept only so old configuration still validates.</summary>
    public int Port { get; set; }
    /// <summary>Loopback port range for the per-session engines. Both 0 = let the OS pick a free port. Never 5001 (legacy bridge); ports that are assigned or already listening are skipped.</summary>
    public int PortRangeStart { get; set; }
    public int PortRangeEnd { get; set; }
    /// <summary>Most sessions (= engines = Chrome instances) this installation may run at once. Each engine uses roughly 300-600 MB of RAM.</summary>
    public int MaxSessions { get; set; } = 5;
    public int MaxCrashes { get; set; } = 5;
    public int BackoffBaseMs { get; set; } = 2000;
    public int BackoffMaxMs { get; set; } = 60000;
    public int HealthIntervalMs { get; set; } = 2000;
    public int StartTimeoutSeconds { get; set; } = 120;
    public int StableSeconds { get; set; } = 120;
    public int InstallTimeoutMinutes { get; set; } = 15;
    public int MinNodeMajor { get; set; } = 18;
}

public sealed class SidecarOptionsValidator : IValidateOptions<SidecarOptions>
{
    public ValidateOptionsResult Validate(string? name, SidecarOptions o)
    {
        var e = new List<string>();
        if (o.Port is < 0 or > 65535) e.Add("Sidecar:Port must be 0 (auto) or 1-65535.");
        if (o.Port == 5001) e.Add("Sidecar:Port 5001 is reserved for the legacy bridge.");
        if (o.MaxSessions is < 1 or > 50) e.Add("Sidecar:MaxSessions must be 1-50.");
        if ((o.PortRangeStart == 0) != (o.PortRangeEnd == 0)) e.Add("Sidecar:PortRangeStart and PortRangeEnd must be set together (or both 0 for automatic ports).");
        if (o.PortRangeStart != 0)
        {
            if (o.PortRangeStart is < 1024 or > 65535 || o.PortRangeEnd is < 1024 or > 65535 || o.PortRangeEnd < o.PortRangeStart) e.Add("Sidecar:PortRangeStart/End must be 1024-65535 with End >= Start.");
            else if (o.PortRangeStart <= 5001 && o.PortRangeEnd >= 5001) e.Add("Sidecar port range must not include 5001 (legacy bridge).");
            else if (o.PortRangeEnd - o.PortRangeStart + 1 < o.MaxSessions) e.Add("Sidecar port range is smaller than Sidecar:MaxSessions.");
        }
        if (o.MaxCrashes is < 1 or > 100) e.Add("Sidecar:MaxCrashes must be 1-100.");
        if (o.BackoffBaseMs < 1 || o.BackoffMaxMs < o.BackoffBaseMs) e.Add("Sidecar:BackoffBaseMs must be positive and not above BackoffMaxMs.");
        if (o.HealthIntervalMs < 10) e.Add("Sidecar:HealthIntervalMs must be at least 10.");
        if (o.StartTimeoutSeconds < 1 || o.StableSeconds < 1 || o.InstallTimeoutMinutes < 1) e.Add("Sidecar timeouts must be positive.");
        if (o.MinNodeMajor < 18) e.Add("Sidecar:MinNodeMajor must be at least 18 (whatsapp-web.js needs it).");
        return e.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(e);
    }
}
