namespace Rasel.Application.Ports;

public enum SidecarState
{
    /// <summary>Not Managed mode (Fake or external Bridge): nothing is supervised.</summary>
    Disabled,
    Preparing,
    InstallingDependencies,
    /// <summary>Prepared and waiting for Start (AutoStart=false).</summary>
    Ready,
    Starting,
    NeedsQr,
    Connected,
    /// <summary>Stopped on purpose (Stop pressed). Never restarted automatically.</summary>
    Stopped,
    /// <summary>Crashed or unhealthy; restarting with back-off.</summary>
    Degraded,
    /// <summary>Cannot continue without a human (Node missing, crash loop, install failed). <see cref="SidecarStatus.Reason"/> says what to do.</summary>
    Failed,
}

/// <param name="Step">Human-readable current step while preparing.</param>
/// <param name="Reason">Plain-language cause and fix for Degraded/Failed. Never contains secrets or phone numbers.</param>
public sealed record SidecarStatus(SidecarState State, string? Step = null, string? Reason = null, int Crashes = 0, bool AutoStart = true);

/// <summary>Result of a start/stop/restart request.</summary>
public enum SidecarCommandResult { Accepted, AlreadyInState, NotManaged }

public interface ISidecarControl
{
    SidecarStatus Status { get; }
    SidecarCommandResult RequestStart();
    SidecarCommandResult RequestStop();
    SidecarCommandResult RequestRestart();
}
