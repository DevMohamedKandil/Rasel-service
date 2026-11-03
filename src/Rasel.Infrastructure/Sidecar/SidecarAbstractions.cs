using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Sidecar;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory, IReadOnlyDictionary<string, string>? Env = null, string? LogPath = null);

public interface IManagedProcess
{
    int Id { get; }
    bool HasExited { get; }
    /// <summary>Completes with the exit code.</summary>
    Task<int> Exited { get; }
    /// <summary>Kills the process and all its children (node and its Chrome).</summary>
    void KillTree();
}

public interface IProcessLauncher
{
    IManagedProcess Start(ProcessSpec spec);
    /// <summary>Runs to completion and returns the exit code and (truncated) stdout; a timeout kills the tree and returns -1.</summary>
    Task<(int ExitCode, string Output)> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct);
}

public sealed record SidecarProbe(bool Alive, bool Ready);

public interface ISidecarProbe
{
    Task<SidecarProbe> ProbeAsync(int port, CancellationToken ct);
}

/// <summary>Where the Bridge provider should send requests for a session. Null while that session's engine is not up (queued sends wait).</summary>
public interface IBridgeEndpoint
{
    (string BaseUrl, string Token)? Resolve(Guid sessionId);
    /// <summary>Managed mode: prepares/starts the session's own engine (async; returns before it is ready). Throws AppException max_sessions_reached.</summary>
    Task EnsureSessionAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Managed mode: stops the session's engine and frees its slot; <paramref name="purge"/> also deletes its data once the process is dead.</summary>
    Task StopSessionAsync(Guid sessionId, bool purge, CancellationToken ct) => Task.CompletedTask;
    /// <summary>What to report while <see cref="Resolve"/> is null. Null = not managed (a missing endpoint is an error).</summary>
    Rasel.Domain.Enums.SessionStatus? NotReadyStatus(Guid sessionId) => null;
}
