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

/// <summary>Where the Bridge provider should send requests. Null while no endpoint is available (queued sends wait).</summary>
public interface IBridgeEndpoint
{
    (string BaseUrl, string Token)? Current { get; }
}
